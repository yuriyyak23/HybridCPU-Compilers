using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Platform.Contracts;

internal static class EhPlanSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("EhPlanFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Fixture", TypeAttributes.Public);
        foreach (string name in new[] { "Valid", "RethrowInTry", "EndFinallyInCatch", "LeaveIntoCatch",
                     "BranchIntoCatch", "SwitchIntoCatch", "RetInTry", "ValidBranchInTry" })
        {
            ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
            Label catchTarget = il.DefineLabel();
            il.BeginExceptionBlock();
            if (name == "RethrowInTry") il.Emit(OpCodes.Rethrow);
            else if (name == "LeaveIntoCatch") il.Emit(OpCodes.Leave, catchTarget);
            else if (name == "BranchIntoCatch") il.Emit(OpCodes.Br, catchTarget);
            else if (name == "SwitchIntoCatch")
            {
                il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Switch, new[] { catchTarget });
            }
            else if (name == "RetInTry") il.Emit(OpCodes.Ret);
            else if (name == "ValidBranchInTry")
            {
                Label localTarget = il.DefineLabel();
                il.Emit(OpCodes.Br, localTarget); il.MarkLabel(localTarget);
                il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Throw);
            }
            else { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Throw); }
            il.BeginCatchBlock(typeof(Exception));
            il.MarkLabel(catchTarget);
            il.Emit(OpCodes.Pop);
            il.Emit(name == "EndFinallyInCatch" ? OpCodes.Endfinally : OpCodes.Rethrow);
            il.EndExceptionBlock();
            il.Emit(OpCodes.Ret);
        }
        MethodBuilder stateMethod = type.DefineMethod("HandlerState", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), new[] { typeof(int) });
        ILGenerator stateIl = stateMethod.GetILGenerator();
        stateIl.DeclareLocal(typeof(int));
        stateIl.DeclareLocal(typeof(int));
        stateIl.Emit(OpCodes.Ldc_I4_1); stateIl.Emit(OpCodes.Stloc_0);
        stateIl.BeginExceptionBlock();
        stateIl.Emit(OpCodes.Ldc_I4_2); stateIl.Emit(OpCodes.Stloc_0);
        stateIl.Emit(OpCodes.Ldnull); stateIl.Emit(OpCodes.Throw);
        stateIl.BeginCatchBlock(typeof(Exception));
        stateIl.Emit(OpCodes.Pop); stateIl.Emit(OpCodes.Ldloc_0); stateIl.Emit(OpCodes.Ldarg_0);
        stateIl.Emit(OpCodes.Add); stateIl.Emit(OpCodes.Stloc_1);
        stateIl.EndExceptionBlock();
        stateIl.Emit(OpCodes.Ldloc_1); stateIl.Emit(OpCodes.Ret);
        foreach (string name in new[] { "UninitializedHome", "InitializedHome", "AssignedHome", "InvalidLocal", "InvalidArgument", "ObjectHome" })
        {
            MethodBuilder state = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
                typeof(void), Type.EmptyTypes);
            state.InitLocals = name is not ("UninitializedHome" or "AssignedHome");
            ILGenerator il = state.GetILGenerator();
            il.DeclareLocal(name == "ObjectHome" ? typeof(object) : typeof(int));
            if (name == "AssignedHome") { il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc_0); }
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Throw);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Pop);
            if (name == "InvalidArgument") il.Emit(OpCodes.Ldarg_0);
            else if (name == "InvalidLocal") il.Emit(OpCodes.Ldloc, (short)7);
            else il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Pop);
            il.EndExceptionBlock();
            il.Emit(OpCodes.Ret);
        }
        foreach (string name in new[] { "BadHandlerStack", "NonemptyRethrow", "LeaveClearsStack" })
        {
            ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
                typeof(void), Type.EmptyTypes).GetILGenerator();
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Throw);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Pop);
            if (name == "BadHandlerStack") il.Emit(OpCodes.Pop);
            else il.Emit(OpCodes.Ldc_I4_1);
            if (name == "NonemptyRethrow") il.Emit(OpCodes.Rethrow);
            il.EndExceptionBlock();
            il.Emit(OpCodes.Ret);
        }
        ILGenerator loopIl = type.DefineMethod("HandlerLoop", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), new[] { typeof(int) }).GetILGenerator();
        loopIl.DeclareLocal(typeof(int));
        loopIl.Emit(OpCodes.Ldc_I4_0); loopIl.Emit(OpCodes.Stloc_0);
        loopIl.BeginExceptionBlock();
        Label loopHead = loopIl.DefineLabel(); loopIl.MarkLabel(loopHead);
        loopIl.Emit(OpCodes.Ldloc_0); loopIl.Emit(OpCodes.Ldc_I4_1); loopIl.Emit(OpCodes.Add); loopIl.Emit(OpCodes.Stloc_0);
        loopIl.Emit(OpCodes.Ldloc_0); loopIl.Emit(OpCodes.Ldarg_0); loopIl.Emit(OpCodes.Blt, loopHead);
        loopIl.Emit(OpCodes.Ldnull); loopIl.Emit(OpCodes.Throw);
        loopIl.BeginCatchBlock(typeof(Exception)); loopIl.Emit(OpCodes.Pop); loopIl.Emit(OpCodes.Ldloc_0); loopIl.Emit(OpCodes.Pop);
        loopIl.EndExceptionBlock(); loopIl.Emit(OpCodes.Ret);
        type.DefineMethod("Plain", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes).GetILGenerator().Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? name, string? code) in new[] { ("UninitializedHome", "HCCIL1818"),
                     ("InvalidLocal", "HCCIL0817"), ("InvalidArgument", "HCCIL0817"),
                     ("InitializedHome", ""), ("AssignedHome", ""), ("ObjectHome", "") })
        {
            ManagedEhImportResultV1 result = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", name));
            if (code.Length != 0)
            {
                if (result.Code != code || result.Plan is not null || result.ControlFlow is not null)
                    throw new Exception($"{name}: expected {code}; got {result.Code} {result.Reason}");
            }
            else if (result.Status != ManagedEhImportStatusV1.Success || result.ControlFlow?.StateHomes is not { Count: 1 } homes ||
                     homes[0].Slot != "local:0" || homes[0].InitializedAtMethodEntry != (name != "AssignedHome") ||
                     homes[0].IsObjectRoot != (name == "ObjectHome"))
                throw new Exception($"{name}: EH homes must preserve exact scalar/root and initlocals classification.");
        }
        ManagedEhImportResultV1 statePlan = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", "HandlerState"));
        if (statePlan.Status != ManagedEhImportStatusV1.Success || statePlan.ControlFlow is not { } stateGraph ||
            !stateGraph.RequiredStateHomes.SequenceEqual(new[] { "arg:0", "local:0" }) ||
            stateGraph.StateHomes.Any(home => home.Type != RestrictedCilTypeV1.Int32 || home.IsObjectRoot) ||
            !stateGraph.LiveStateAtEntry[statePlan.Plan!.Clauses[0].TryOffset + 1].Contains("local:0"))
            throw new Exception("Exceptional liveness must retain pre-store local and argument state, not handler-defined result slots.");
        MethodInfo loadedState = Assembly.Load(stream.ToArray()).GetType("Fixture")!.GetMethod("HandlerState")!;
        ManagedEhMethodPlanV1 authenticPlan = statePlan.Plan!;
        IReadOnlyList<HybridCpuFrameSlotRequestV2> frameRequests = ManagedEhFrameHomesV1.CreateRequests(authenticPlan);
        if (!frameRequests.Select(slot => slot.Identity).SequenceEqual(new[] { "eh-home:arg:0", "eh-home:local:0" }) ||
            frameRequests.Any(slot => slot.SizeBytes != 8 || slot.AlignmentBytes != 8))
            throw new Exception("EH scalar state must reserve separate aligned ABI-word homes.");
        bool rejectedByrefHome = false;
        try
        {
            ManagedEhFrameHomesV1.CreateRequests(authenticPlan with { ControlFlow = stateGraph with
            { StateHomes = stateGraph.StateHomes.Select(home => home with { Type = RestrictedCilTypeV1.ManagedByRef }).ToArray() } });
        }
        catch (InvalidOperationException) { rejectedByrefHome = true; }
        if (!rejectedByrefHome) throw new Exception("EH scalar homes must not silently admit byref storage.");
        foreach ((string? name, string? code) in new[] { ("BadHandlerStack", "HCCIL1602"),
                     ("NonemptyRethrow", "HCCIL0819") })
        {
            ManagedEhImportResultV1 plan = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", name));
            RestrictedCilImportResultV1 result = importer.ImportImage(stream.ToArray(), new("Fixture", name), managedEhPlan: plan.Plan);
            if (result.Program is not null || !result.Diagnostics.Any(d => d.Code == code))
                throw new Exception($"{name}: expected typed EH diagnostic {code}, got {string.Join(';', result.Diagnostics)}");
        }
        ManagedEhImportResultV1 leavePlan = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", "LeaveClearsStack"));
        RestrictedCilImportResultV1 leaveClears = importer.ImportImage(stream.ToArray(), new("Fixture", "LeaveClearsStack"), managedEhPlan: leavePlan.Plan);
        if (leaveClears.Status != RestrictedCilImportStatusV1.Success || leaveClears.Program is null ||
            leaveClears.ManagedEhLoweringEvidence?.LeaveTransferCount != 1 || !leaveClears.RequiresNativeExceptionTransfer ||
            !leaveClears.Program.Instructions.Any(instruction => instruction.Annotation.BranchTargetSymbolName ==
                HybridCpuManagedEhScopeEmitterV1.LeaveCatchSymbol))
            throw new Exception($"Reachable leave must clear the evaluation stack and survive admitted EH lowering: " +
                $"status={leaveClears.Status}; diagnostics={string.Join(';', leaveClears.Diagnostics)}");
        ManagedEhImportResultV1 validPlan = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", "Valid"));
        RestrictedCilImportResultV1 validImport = importer.ImportImage(stream.ToArray(), new("Fixture", "Valid"), managedEhPlan: validPlan.Plan);
        if (validImport.Status != RestrictedCilImportStatusV1.Success || validImport.Program is null ||
            !validImport.Program.Instructions.Any(instruction => instruction.Annotation.BranchTargetSymbolName ==
                HybridCpuManagedEhScopeEmitterV1.RethrowSymbol) ||
            HybridCpuManagedEhScopeEmitterV1.EmitRethrowObject().Status != HybridCpuObjectStatusV1.Success ||
            HybridCpuManagedEhScopeEmitterV1.EmitLeaveCatchObject().Status != HybridCpuObjectStatusV1.Success)
            throw new Exception("Rethrow and catch-scope leave require exact native helper bindings: " +
                $"status={validImport.Status}; diagnostics={string.Join(';', validImport.Diagnostics)}; " +
                $"targets={string.Join(',', validImport.Program?.Instructions.Select(static instruction => instruction.Annotation.BranchTargetSymbolName).Where(static target => target is not null) ?? [])}.");
        ManagedEhImportResultV1 bounded = new RestrictedCilImporterV1(RestrictedCilImportBudgetsV1.Production with { MaximumBasicBlocks = 1 },
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportManagedEhPlan(stream.ToArray(), new("Fixture", "HandlerState"));
        if (bounded.Status != ManagedEhImportStatusV1.BudgetExhausted || bounded.Code != "HCCIL2804" || bounded.Plan is not null)
            throw new Exception("EH block construction must fail closed at its configured budget.");
        if (authenticPlan.ControlFlow?.Digest != stateGraph.Digest)
            throw new Exception("The graph-owned EH plan must retain its validated state analysis.");
        ManagedEhMethodPlanV1 wrongClause = authenticPlan with
        {
            Clauses = authenticPlan.Clauses.Select(clause => clause with { HandlerOffset = clause.HandlerOffset + 1 }).ToArray()
        };
        ManagedEhMethodPlanV1 rehashedWrongClause = wrongClause with
        {
            ContractDigest = ManagedEhPlanContractV1.ComputeDigest(wrongClause.MethodIdentity,
                wrongClause.InstructionIdentityPrefix, wrongClause.MethodBodySize, wrongClause.Clauses, wrongClause.Operations)
        };
        foreach (ManagedEhMethodPlanV1? forged in new[] { wrongClause, rehashedWrongClause,
                     authenticPlan with { Operations = [] }, authenticPlan with { InstructionIdentityPrefix = "other-pe:il_" } })
        {
            RestrictedCilImportResultV1 rejected = importer.ImportImage(stream.ToArray(), new("Fixture", "HandlerState"), managedEhPlan: forged);
            if (rejected.Status != RestrictedCilImportStatusV1.InvalidInput ||
                !rejected.Diagnostics.Any(d => d.Code == "HCCIL0810") || rejected.Program is not null || rejected.ManagedEhAnalysis is not null)
                throw new Exception("Same-sized or rehashed fabricated EH plans must not become lowering inputs.");
        }
        ManagedEhMethodPlanV1 forgedState = authenticPlan with
        {
            ControlFlow = stateGraph with { StateHomes = [], Edges = [], Digest = "untrusted" }
        };
        RestrictedCilImportResultV1 admittedAnalysis = importer.ImportImage(stream.ToArray(), new("Fixture", "HandlerState"), managedEhPlan: forgedState);
        if (admittedAnalysis.Status != RestrictedCilImportStatusV1.Success || admittedAnalysis.Program is null || admittedAnalysis.Diagnostics.Count != 0 ||
            admittedAnalysis.ManagedEhAnalysis?.ControlFlow is not { } rebound ||
            rebound.Digest != stateGraph.Digest || rebound.StateHomes.Count != 2 || rebound.Edges.Count != stateGraph.Edges.Count)
            throw new Exception("Importer must reconstruct exact EH state from the PE, while keeping native lowering gated.");
        ManagedEhTypedDataflowV1 typed = admittedAnalysis.ManagedEhTypedDataflow ?? throw new Exception("Verified EH states must survive import.");
        ManagedEhHomeAccessPlanV1 accessPlan = ManagedEhFrameHomesV1.CreateAccessPlan(authenticPlan, typed);
        ManagedEhLoweringEvidenceV1 loweringEvidence = admittedAnalysis.ManagedEhLoweringEvidence ??
            throw new Exception("EH gate must retain structural lowering evidence without exposing a publishable program.");
        if (loweringEvidence.HomeAccessInstructionCount != accessPlan.Accesses.Count ||
            loweringEvidence.CatchEntryCopyCount != 1 ||
            loweringEvidence.LeaveTransferCount != authenticPlan.LeaveTransfers.Count(transfer =>
                typed.Entries.Any(entry => entry.IlOffset == transfer.IlOffset)) ||
            loweringEvidence.ThrowTransferCount != authenticPlan.Operations.Count(operation =>
                operation.Kind == ManagedEhOperationKindV1.Throw && typed.Entries.Any(entry => entry.IlOffset == operation.IlOffset)) ||
            loweringEvidence.RethrowTransferCount != authenticPlan.Operations.Count(operation =>
                operation.Kind == ManagedEhOperationKindV1.Rethrow && typed.Entries.Any(entry => entry.IlOffset == operation.IlOffset)) ||
            loweringEvidence.EndFinallyTransferCount != authenticPlan.Operations.Count(operation =>
                operation.Kind == ManagedEhOperationKindV1.EndFinally && typed.Entries.Any(entry => entry.IlOffset == operation.IlOffset)) ||
            string.IsNullOrWhiteSpace(loweringEvidence.Digest))
            throw new Exception("EH lowering evidence must bind every home, catch ABI copy, leave and rethrow.");
        string[] ehCallees = admittedAnalysis.Program!.Instructions
            .Select(static instruction => instruction.Annotation.BranchTargetSymbolName)
            .Where(static target => !string.IsNullOrWhiteSpace(target)).Cast<string>()
            .Distinct(StringComparer.Ordinal).ToArray();
        ManagedCompiledMethodV1 backendMethod = new ManagedCompiledMethodV1(
            new("managed-method-identity/v1", "EhPlanFixture", "Main", 1, "Fixture", "HandlerState",
                "probe", admittedAnalysis.Provenance!.MethodIdentity, "probe"),
            admittedAnalysis, ehCallees);
        ScalarControlFlowV2MethodObjectV1 ehObject = (ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
            .GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { backendMethod, 0 })!;
        if (ehObject.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success ||
            ehObject.FrameSizeBytes < frameRequests.Sum(static slot => slot.SizeBytes) ||
            ehObject.GcInfoDigest is null || ehObject.UnwindInfoDigest is null || ehObject.EhInfoDigest is null ||
            !ehObject.ObjectArtifact.Sections.Any(static section => section.Name == ".hcgc") ||
            !ehObject.ObjectArtifact.Sections.Any(static section => section.Name == ".hceh"))
            throw new Exception("EH symbolic homes must survive scheduling/RA and resolve into the final native frame.");
        if (!ehObject.ObjectArtifact.Symbols.Any(symbol => symbol.Name ==
                ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(ehObject.MethodIdentity, "gc") && symbol.IsDefinition) ||
            !ehObject.ObjectArtifact.Symbols.Any(symbol => symbol.Name ==
                ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(ehObject.MethodIdentity, "unwind") && symbol.IsDefinition) ||
            !ehObject.ObjectArtifact.Symbols.Any(symbol => symbol.Name ==
                ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(ehObject.MethodIdentity, "eh") && symbol.IsDefinition))
            throw new Exception("Final GC/unwind/EH sections require deterministic link-visible symbols.");
        HybridCpuObjectArtifactV1 dispatchTable = ManagedEhDispatchTableObjectV1.Emit([new(ehObject.MethodIdentity,
            ehObject.CodeBytes, ehObject.GcInfo!.Length, ehObject.UnwindInfo!.Length, ehObject.EhInfo!.Length,
            ehObject.FinallyInfo?.Length ?? 0, HybridCpuManagedUnwindCodecV2.Decode(ehObject.UnwindInfo))],
            [new(1, 100, 0), new(2, 200, 100)], HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize);
        HybridCpuManagedUnwindRecordV2 handlerFreeUnwind = new HybridCpuManagedUnwindRecordV2(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, 0, HybridCpuNativeAbiContractV2.ReturnAddressRegister, null, []);
        HybridCpuObjectArtifactV1 handlerFreeTable = ManagedEhDispatchTableObjectV1.Emit([new("HandlerFree", 256, 16, 28, 0, 0, handlerFreeUnwind)]);
        if (handlerFreeTable.Status != HybridCpuObjectStatusV1.Success ||
            handlerFreeTable.Relocations.Any(relocation => relocation.TargetSymbol ==
                ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol("HandlerFree", "eh")))
            throw new Exception("Handler-free managed frames must remain in the unwind index without inventing EH metadata.");
        try
        {
            _ = ManagedEhDispatchTableObjectV1.Emit([new("Bad", 256, 16, 28, -1, 0, handlerFreeUnwind)]);
            throw new Exception("Negative EH metadata size must fail closed.");
        }
        catch (ArgumentException) { }
        HybridCpuObjectArtifactV1 ehDataObject = new HybridCpuObjectWriterV1().Write(new(
            ehObject.ObjectArtifact.Sections,
            ehObject.ObjectArtifact.Symbols.Where(static symbol => symbol.IsDefinition).ToArray(), [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        HybridCpuStaticLinkArtifactV1 linkedEh = new HybridCpuStaticLinkerV1().Link([
            new(ehObject.ModuleIdentity, ehDataObject.Bytes),
            new(ManagedEhDispatchTableObjectV1.ModuleIdentity, dispatchTable.Bytes)]);
        if (linkedEh.Status != HybridCpuLinkStatusV1.Success)
            throw new Exception("Image-native EH dispatch index must resolve all method/metadata addresses: " +
                string.Join(';', linkedEh.Diagnostics.Select(static diagnostic => diagnostic.Code + ':' + diagnostic.Message)));
        HybridCpuLinkedSectionV1 tableSection = linkedEh.Sections.Single(section =>
            section.ModuleIdentity == ManagedEhDispatchTableObjectV1.ModuleIdentity);
        int tableOffset = checked((int)(tableSection.Address - linkedEh.ImageBase));
        ReadOnlySpan<byte> tableBytes = linkedEh.ImageBytes.AsSpan(tableOffset, checked((int)tableSection.Size));
        HybridCpuLinkedSymbolV1 methodSymbol = linkedEh.Symbols.Single(symbol => symbol.Name == ehObject.MethodIdentity);
        HybridCpuLinkedSymbolV1 ehSymbol = linkedEh.Symbols.Single(symbol => symbol.Name ==
            ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(ehObject.MethodIdentity, "eh"));
        if (BinaryPrimitives.ReadUInt32LittleEndian(tableBytes) != HybridCpuManagedEhDispatchIndexV1.Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(tableBytes[HybridCpuManagedEhDispatchIndexV1.CountOffset..]) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(tableBytes[HybridCpuManagedEhDispatchIndexV1.TypeCountOffset..]) != 2 ||
            BinaryPrimitives.ReadUInt64LittleEndian(tableBytes[HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes..]) != methodSymbol.Address ||
            BinaryPrimitives.ReadUInt64LittleEndian(tableBytes[(HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + 16)..]) != ehSymbol.Address ||
            BinaryPrimitives.ReadUInt64LittleEndian(tableBytes[(HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + HybridCpuManagedEhDispatchIndexV1.RowSizeBytes + HybridCpuManagedEhDispatchIndexV1.TypeIdOffset)..]) != 100 ||
            BinaryPrimitives.ReadUInt64LittleEndian(tableBytes[(HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + HybridCpuManagedEhDispatchIndexV1.RowSizeBytes + HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes + HybridCpuManagedEhDispatchIndexV1.BaseTypeIdOffset)..]) != 100)
            throw new Exception("EH dispatch index absolute relocations must bind exact final image addresses.");
        HybridCpuRestrictedImageV1 ehImage = new HybridCpuRestrictedImageBuilderV1().Build(new(linkedEh, ehObject.MethodIdentity,
            GlobalPointerSymbol: ManagedEhDispatchTableObjectV1.Symbol));
        HybridCpuLinkedSymbolV1 tableSymbol = linkedEh.Symbols.Single(symbol =>
            symbol.Name == ManagedEhDispatchTableObjectV1.Symbol);
        if (ehImage.Status != HybridCpuStartupStatusV1.Success ||
            ehImage.InitialRegisters?.GlobalPointerRegister != HybridCpuNativeAbiContractV2.GlobalPointerRegister ||
            ehImage.InitialRegisters.GlobalPointer != tableSymbol.Address)
            throw new Exception("Restricted image startup must bind ABI x3 to the final read-only EH dispatch index.");
        HybridCpuRestrictedImageV1 inspectedEhImage = new HybridCpuRestrictedImageBuilderV1().Inspect(ehImage.PackageBytes);
        if (inspectedEhImage.Status != HybridCpuStartupStatusV1.Success ||
            inspectedEhImage.InitialRegisters?.GlobalPointer != tableSymbol.Address)
            throw new Exception("Restricted package encoding must preserve the exact EH global pointer.");
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = new HybridCpuManagedBootstrapDescriptorBuilderV1().Create(linkedEh,
            ehObject.MethodIdentity, ehObject.MethodIdentity,
            [new(ehObject.MethodIdentity, ehObject.GcInfo!,
                new HybridCpuManagedUnwindRecordV1(HybridCpuManagedFrameKindV1.Managed,
                    HybridCpuManagedCfaBaseV1.StackPointer, 0, 0, []),
                ehObject.UnwindInfo, ehObject.EhInfo)], []);
        HybridCpuRestrictedImageV1 bootstrappedEhImage = new HybridCpuRestrictedImageBuilderV1().Build(new(linkedEh,
            ehObject.MethodIdentity, HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes,
            bootstrap, ManagedEhDispatchTableObjectV1.Symbol));
        HybridCpuRestrictedImageV1 inspectedBootstrappedEhImage = new HybridCpuRestrictedImageBuilderV1().Inspect(bootstrappedEhImage.PackageBytes);
        if (bootstrappedEhImage.Status != HybridCpuStartupStatusV1.Success ||
            inspectedBootstrappedEhImage.Status != HybridCpuStartupStatusV1.Success ||
            inspectedBootstrappedEhImage.InitialRegisters?.GlobalPointer != tableSymbol.Address ||
            inspectedBootstrappedEhImage.RuntimeBootstrap?.DescriptorDigest != bootstrap.DescriptorDigest)
            throw new Exception("Non-empty bootstrap metadata must begin after and preserve the x3 package fields.");
        ManagedEhImportResultV1 objectPlan = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", "ObjectHome"));
        RestrictedCilImportResultV1 objectImport = importer.ImportImage(stream.ToArray(), new("Fixture", "ObjectHome"), managedEhPlan: objectPlan.Plan);
        IrProgramSchedule objectSchedule = new HybridCpuLocalListScheduler().ScheduleProgram(objectImport.Program!);
        IrProgramBundlingResult objectBundles = new HybridCpuBundleFormer().BundleProgram(objectSchedule);
        HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
        IrRegisterAllocationResultV1 objectAllocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(objectSchedule, objectBundles,
            resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification,
            fixedFrameSlots: ManagedEhFrameHomesV1.CreateRequests(objectPlan.Plan!));
        HybridCpuManagedFixedRootRequestV1[] fixedRoots = objectPlan.Plan!.ControlFlow!.StateHomes.Where(static home => home.IsObjectRoot)
            .Select(static home => new HybridCpuManagedFixedRootRequestV1($"eh-root:{home.Slot}",
                $"eh-home:{home.Slot}", HybridCpuGcReferenceKindV1.ObjectReference)).ToArray();
        EhFinalizerCancellationSmoke.Run(objectPlan.Plan!, objectAllocation);
        GcFinalizerCancellationSmoke.Run(objectImport.Provenance!.MethodIdentity, objectAllocation, fixedRoots);
        HybridCpuManagedMetadataArtifactV1 rootMaps = new HybridCpuManagedMetadataFinalizerV1().FinalizeRequiredCallSites(
            objectImport.Provenance!.MethodIdentity, 0, objectAllocation,
            HybridCpuManagedMetadataOptionsV1.Qualification, fixedRoots);
        if (rootMaps.Status != HybridCpuManagedMetadataStatusV1.Finalized || rootMaps.Safepoints.Count == 0 ||
            rootMaps.Safepoints.Any(point => !point.LiveReferences.Any(reference =>
                reference.ValueIdentity == "eh-root:local:0" && reference.LocationKind == HybridCpuGcLocationKindV1.Stack)))
            throw new Exception("Object-reference EH homes must be present at every final call safepoint as exact stack roots: " + rootMaps.Reason);
        if (!accessPlan.Accesses.Any(access => access.Kind == ManagedEhHomeAccessKindV1.Initialize && access.Slot == "arg:0") ||
            !accessPlan.Accesses.Any(access => access.Kind == ManagedEhHomeAccessKindV1.Initialize && access.Slot == "local:0") ||
            !accessPlan.Accesses.Any(access => access.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition &&
                access.Slot == "local:0" && access.IlOffset == authenticPlan.Clauses[0].TryOffset + 1) ||
            !accessPlan.Accesses.Any(access => access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry &&
                access.IlOffset == authenticPlan.Clauses[0].HandlerOffset) || accessPlan.Accesses.Any(access => access.IsObjectRoot))
            throw new Exception("EH home plan must initialize, store definitions and reload exact scalar state at handlers.");
        ManagedEhHomeAccessV1 zeroInit = accessPlan.Accesses.Single(access => access.Kind == ManagedEhHomeAccessKindV1.Initialize && access.Slot == "local:0");
        ManagedEhHomeAccessV1 argumentInit = accessPlan.Accesses.Single(access => access.Kind == ManagedEhHomeAccessKindV1.Initialize && access.Slot == "arg:0");
        ManagedEhHomeAccessV1 reloadAccess = accessPlan.Accesses.First(access => access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry);
        if (zeroInit.ValueKind != ManagedEhHomeValueKindV1.ZeroConstant || zeroInit.ConstantValue != 0 ||
            argumentInit.ValueKind != ManagedEhHomeValueKindV1.ExistingSsaValue ||
            reloadAccess.ValueKind != ManagedEhHomeValueKindV1.ReloadDefinition || reloadAccess.ConstantValue is not null)
            throw new Exception("EH access provenance must distinguish zero, existing SSA values and reload definitions.");
        IrOperand exactArgument = new IrOperand(IrOperandKind.VirtualValue, 0, argumentInit.ValueIdentity);
        if (ManagedEhFrameHomesV1.ResolveExactOperand(zeroInit, new Dictionary<string, IrOperand>()).Value != 0 ||
            ManagedEhFrameHomesV1.ResolveExactOperand(argumentInit,
                new Dictionary<string, IrOperand> { [argumentInit.ValueIdentity] = exactArgument }) != exactArgument ||
            ManagedEhFrameHomesV1.ResolveExactOperand(reloadAccess, new Dictionary<string, IrOperand>()).Name != reloadAccess.ValueIdentity)
            throw new Exception("EH access resolver must bind only exact SSA identities and architectural zero.");
        ManagedEhHomeAccessV1 nonzero = accessPlan.Accesses.First(access => access.ValueKind == ManagedEhHomeValueKindV1.NonZeroConstant);
        bool nonzeroRejected = false;
        try { ManagedEhFrameHomesV1.ResolveExactOperand(nonzero, new Dictionary<string, IrOperand>()); }
        catch (InvalidOperationException) { nonzeroRejected = true; }
        if (!nonzeroRejected) throw new Exception("Nonzero home constants require an explicit target materialization instruction.");
        IrInstruction insertionOrigin = importer.ImportImage(stream.ToArray(), new("Fixture", "Plain")).Program!
            .Instructions.First();
        Dictionary<int, IrInstruction> origins = accessPlan.Accesses.Select(access => access.IlOffset).Distinct().ToDictionary(offset => offset, _ => insertionOrigin);
        IReadOnlyList<ManagedEhHomeInsertionV1> insertions = ManagedEhFrameHomesV1.MaterializeSymbolic(accessPlan, origins, access =>
            new(IrOperandKind.ArchitecturalRegister,
                access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry ? 11UL : 10UL,
                access.ValueIdentity));
        if (insertions.Count != accessPlan.Accesses.Count || insertions.Any(row =>
                row.Instruction.Annotation.FixedFrameSlotIdentity != $"eh-home:{row.Access.Slot}" ||
                row.Placement != (row.Access.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition
                    ? ManagedEhInsertionPlacementV1.After : ManagedEhInsertionPlacementV1.Before) ||
                row.Instruction.Opcode != (row.Access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry
                    ? HybridCpuOpcode.LD : HybridCpuOpcode.SD)))
            throw new Exception("EH home materializer must preserve exact IL anchor, before/after placement and symbolic slot.");
        bool missingAnchorRejected = false;
        try { ManagedEhFrameHomesV1.MaterializeSymbolic(accessPlan, new Dictionary<int, IrInstruction>(), _ =>
            new(IrOperandKind.ArchitecturalRegister, 10, "value")); }
        catch (InvalidOperationException) { missingAnchorRejected = true; }
        if (!missingAnchorRejected) throw new Exception("EH home insertion without an exact IL-to-IR anchor must fail closed.");
        int storeOffset = authenticPlan.Clauses[0].TryOffset + 1;
        ManagedEhTypedStateV1 beforeStore = typed.EdgeStates.Single(row => row.Edge.SourceOffset == storeOffset &&
            row.Edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate).State;
        ManagedEhTypedStateV1 afterStore = typed.EdgeStates.Single(row => row.Edge.SourceOffset == storeOffset &&
            row.Edge.Kind == ManagedEhEdgeKindV1.Normal).State;
        if (beforeStore.Locals[0].Constant != 1 || afterStore.Locals[0].Constant != 2 ||
            beforeStore.Stack.Count != 1 || beforeStore.Stack[0].Type != RestrictedCilTypeV1.ObjectReference)
            throw new Exception("Exceptional state must preserve the pre-store local and replace the evaluation stack.");
        ManagedEhImportResultV1 loopPlan = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", "HandlerLoop"));
        RestrictedCilImportResultV1 loopImport = importer.ImportImage(stream.ToArray(), new("Fixture", "HandlerLoop"), managedEhPlan: loopPlan.Plan);
        ManagedEhTypedDataflowV1? loopTyped = loopImport.ManagedEhTypedDataflow;
        if (loopTyped is null || loopImport.Status != RestrictedCilImportStatusV1.Success || loopImport.Program is null ||
            !loopTyped.Entries.Any(state => state.Locals.Any(value => value.Identity.StartsWith("eh:join:", StringComparison.Ordinal))))
            throw new Exception("EH loop state must converge to explicit join identities without emitting a native program: " +
                $"status={loopImport.Status}; program={loopImport.Program is not null}; " +
                string.Join(';', loopImport.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}:{diagnostic.StableSourceIdentity}")));
        ManagedEhHomeAccessPlanV1 loopAccesses = ManagedEhFrameHomesV1.CreateAccessPlan(loopPlan.Plan!, loopTyped);
        if (loopAccesses.Accesses.Count(access => access.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition &&
                access.Slot == "local:0") != 1 ||
            !loopAccesses.Accesses.Any(access => access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry))
            throw new Exception("EH loop home stores must follow definitions, not duplicate per outgoing edge or iteration.");
        RestrictedCilImportResultV1 loopRepeat = importer.ImportImage(stream.ToArray(), new("Fixture", "HandlerLoop"), managedEhPlan: loopPlan.Plan);
        if (loopRepeat.ManagedEhTypedDataflow?.Digest != loopTyped.Digest)
            throw new Exception("EH typed entries and current edge states must have a deterministic digest.");
        if (ManagedEhFrameHomesV1.CreateAccessPlan(loopPlan.Plan!, loopRepeat.ManagedEhTypedDataflow!).Digest != loopAccesses.Digest)
            throw new Exception("EH physical-home access plan must be deterministic.");
        if ((int)loadedState.Invoke(null, new object[] { 40 })! != 42)
            throw new Exception("CoreCLR handler state parity fixture failed.");
        foreach ((string? name, string? code) in new[] { ("Valid", ""), ("RethrowInTry", "HCCIL0812"),
                     ("EndFinallyInCatch", "HCCIL0813"), ("LeaveIntoCatch", "HCCIL0814"),
                     ("BranchIntoCatch", "HCCIL0816"), ("SwitchIntoCatch", "HCCIL0816"),
                     ("RetInTry", "HCCIL0815"), ("ValidBranchInTry", "") })
        {
            ManagedEhImportResultV1 result = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", name));
            if (code.Length == 0 ? result.Status != ManagedEhImportStatusV1.Success : result.Code != code || result.Plan is not null)
                throw new Exception(name + ": " + result.Code + " " + result.Reason);
            if (result.Plan is not null && result.Plan.HandlerEntries.Any(entry =>
                entry.ExceptionReferenceRegister != 10 ||
                !entry.EvaluationStack.SequenceEqual(new[] { RestrictedCilTypeV1.ObjectReference })))
                throw new Exception("Catch entry must consume the rooted runtime exception in x10 as one stack object.");
            if (result.Plan is not null)
            {
                ManagedEhControlFlowV1 graph = result.ControlFlow ?? throw new Exception("EH plan must include its instruction CFG.");
                if (!graph.Blocks.SelectMany(block => block.InstructionOffsets).SequenceEqual(graph.InstructionOffsets) ||
                    graph.Blocks.Any(block => block.InstructionOffsets[0] != block.StartOffset) ||
                    result.Plan.Clauses.Any(clause => !graph.Blocks.Any(block =>
                        block.StartOffset == clause.HandlerOffset && block.HandlerClauseOrdinals.Contains(clause.Ordinal))) ||
                    graph.BlockEdges.Count(edge => edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate) !=
                        graph.Edges.Count(edge => edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate) ||
                    graph.BlockEdges.Any(edge => !graph.Blocks[edge.SourceBlock].InstructionOffsets.Contains(edge.SourceOffset)))
                    throw new Exception("EH basic blocks must partition IL and preserve every exceptional source instruction/handler entry.");
                if (graph.Edges.Any(edge => !graph.InstructionOffsets.Contains(edge.SourceOffset) ||
                        !graph.InstructionOffsets.Contains(edge.TargetOffset)) ||
                    !graph.Edges.Any(edge => edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate) ||
                    graph.Edges.Where(edge => edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate)
                        .Any(edge => edge.ClauseOrdinal is null || !result.Plan.Clauses.Any(clause =>
                            clause.Ordinal == edge.ClauseOrdinal && clause.HandlerOffset == edge.TargetOffset)) ||
                    result.Plan.Operations.Where(operation => operation.Kind is ManagedEhOperationKindV1.Throw or ManagedEhOperationKindV1.Rethrow)
                        .Any(operation => graph.Edges.Any(edge => edge.SourceOffset == operation.IlOffset &&
                            edge.Kind == ManagedEhEdgeKindV1.Normal)))
                    throw new Exception("EH CFG must separate runtime dispatch from normal successors and terminate throw/rethrow.");
                ManagedEhImportResultV1 repeated = importer.ImportManagedEhPlan(stream.ToArray(), new("Fixture", name));
                if (repeated.ControlFlow?.Digest != graph.Digest)
                    throw new Exception("EH CFG digest must be repeatable.");
            }
        }
        ManagedEhMethodPlanV1 nested = new ManagedEhMethodPlanV1("nested", "cil:nested", 200,
            [new(HybridCPU.Platform.Contracts.HybridCpuManagedEhClauseKindV1.Catch, 0, 20, 20, 60, "Exception", 1, 0),
             new(HybridCPU.Platform.Contracts.HybridCpuManagedEhClauseKindV1.Finally, 30, 10, 40, 10, null, 0, 1),
             new(HybridCPU.Platform.Contracts.HybridCpuManagedEhClauseKindV1.Finally, 0, 100, 100, 10, null, 0, 2)],
            [new(ManagedEhOperationKindV1.Leave, 35, 150, ""),
             new(ManagedEhOperationKindV1.Leave, 35, 60, "")], "analysis-fixture");
        ManagedEhLeaveTransferV1 exit = nested.LeaveTransfers[0];
        ManagedEhFinallyContinuationPlanV1 continuations = nested.FinallyContinuations;
        if (!exit.ClearsEvaluationStack || !exit.Actions.Select(action => action.ClauseOrdinal).SequenceEqual(new[] { 1, 0, 2 }) ||
            exit.Actions[1].Kind != ManagedEhLeaveActionKindV1.ReleaseCatchScope ||
            !nested.LeaveTransfers[1].Actions.Select(action => action.ClauseOrdinal).SequenceEqual(new[] { 1 }) ||
            continuations.ExceptionalToken != ManagedEhFinallyContinuationPlanV1.ReservedExceptionalToken ||
            continuations.Continuations.Count != 2 ||
            !continuations.Continuations[0].Steps.Select(static step => step.ClauseOrdinal).SequenceEqual(new[] { 1, 2 }) ||
            continuations.Continuations[0].Steps[0].NextClauseOrdinal != 2 ||
            continuations.Continuations[0].Steps[0].ReleaseCatchScopesBeforeEntry != 0 ||
            continuations.Continuations[0].Steps[1].ReleaseCatchScopesBeforeEntry != 1 ||
            continuations.Continuations[0].Steps[1].NextClauseOrdinal is not null ||
            continuations.Continuations[0].Steps[1].NextOffset != 150 ||
            continuations.Continuations[0].ReleaseCatchScopesBeforeTarget != 0 ||
            continuations.Continuations[1].Steps is not [{ ClauseOrdinal: 1, ReleaseCatchScopesBeforeEntry: 0, NextClauseOrdinal: null, NextOffset: 60 }] ||
            nested.FinallyContinuations.Digest != continuations.Digest ||
            nested.HandlerEntries.Where(entry => entry.ClauseOrdinal != 0).Any(entry =>
                entry.EvaluationStack.Count != 0 || entry.ExceptionReferenceRegister is not null))
            throw new Exception("leave must produce deterministic normal-continuation tokens for inner/outer finally ordering; exceptional token zero remains reserved.");
        Console.WriteLine("PASS compiler EH plan handler-context checks and invalid leave/rethrow/endfinally rejection");
    }
}
