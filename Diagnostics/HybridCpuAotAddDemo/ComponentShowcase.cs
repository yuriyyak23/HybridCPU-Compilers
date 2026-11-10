using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.Compiler.Release;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCpuAotAddDemo;

internal static class ComponentShowcase
{
    public static async Task<int> Main()
    {
        Console.WriteLine("HybridCPU RefPlan7 component showcase");
        Console.WriteLine("Managed publish: restricted seven-workstream graph subset; remaining component workstreams are opt-in/default-off.\n");

        List<(string Phase, string Feature, string Result)> rows = new List<(string Phase, string Feature, string Result)>();
        try
        {
            AddContractCatalog(rows);
            DemonstrateCilAndManagedEh(rows);
            DemonstrateHeapShapesAndGc(rows);
            DemonstrateDispatchDelegatesAndGenerics(rows);
            DemonstrateKernelThreadsTimersAndSynchronization(rows);
            DemonstrateInteropAndReflection(rows);
            await DemonstrateAsync(rows);
            DemonstrateFinalReleaseManifest(rows);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        foreach ((string phase, string feature, string result) in rows)
            Console.WriteLine($"[{phase,-5}] {feature,-38} {result}");
        Console.WriteLine($"\nCompleted {rows.Count} RefPlan7 checks.");
        return 0;
    }

    private static void AddContractCatalog(List<(string, string, string)> rows)
    {
        HybridCpuRuntimePackManifestV1 pack = HybridCpuSdkPackContractV1.CreateManifest();
        Require(pack.PackVersion == "1.15.15-refplan7-phase15", "stale SDK pack identity");
        Require(pack.PublishQualifiedWorkstreams.SequenceEqual(HybridCpuSdkPackContractV1.PublishQualifiedWorkstreams),
            "managed publish workstream set is stale or non-canonical");
        rows.Add(("00/01", "platform + managed ABI", $"{HybridCpuPlatformContractV1.SchemaMajor}.{HybridCpuPlatformContractV1.SchemaMinor}; ABI {HybridCpuManagedAbiFamilyV1.SchemaMajor}.{HybridCpuManagedAbiFamilyV1.SchemaMinor}"));
        rows.Add(("01-15", "qualified component workstreams", $"{pack.QualifiedWorkstreams.Count}; publish={pack.PublishQualifiedWorkstreams.Count}"));
        rows.Add(("16", "runtime pack", $"{pack.PackVersion} / {Short(pack.ContractDigest)}"));
        Require(HybridCpuManagedFeatureSetV1.Default.Workstreams.Count == 20, "unexpected feature-set size");
        Require(HybridCpuManagedFeatureSetV1.Default.Workstreams.Count(static row =>
            row.Support == HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff) == 18,
            "unexpected qualified workstream count");
    }

    private static void DemonstrateCilAndManagedEh(List<(string, string, string)> rows)
    {
        byte[] image = File.ReadAllBytes(typeof(LanguageShowcase).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 scalar = importer.ImportImage(image,
            new(typeof(EntryPoint).FullName!, nameof(EntryPoint.NestedBranch)), "refplan7-demo");
        Require(scalar.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", scalar.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));

        ManagedEhImportResultV1 eh = importer.ImportManagedEhPlan(image,
            new(typeof(LanguageShowcase).FullName!, nameof(LanguageShowcase.ExceptionAndFinally)));
        Require(eh.Status == ManagedEhImportStatusV1.Success, $"{eh.Code}: {eh.Reason}");
        Require(eh.Plan!.Clauses.Any(static row => row.Kind == HybridCpuManagedEhClauseKindV1.Catch), "catch missing");
        Require(eh.Plan.Clauses.Any(static row => row.Kind == HybridCpuManagedEhClauseKindV1.Finally), "finally missing");

        MethodInfo async = typeof(LanguageShowcase).GetMethod(nameof(LanguageShowcase.AsyncAndCancellationAsync))!;
        Type stateMachine = async.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        ManagedEhImportResultV1 asyncEh = importer.ImportManagedEhPlan(image, new(stateMachine.Name, "MoveNext"));
        Require(asyncEh.Status == ManagedEhImportStatusV1.Success, $"async MoveNext: {asyncEh.Code} {asyncEh.Reason}");

        HybridCpuManagedTypeSystemV1 exceptionTypes = BuildTypes(
            new HybridCpuManagedTypeDeclarationV1("System.Exception", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new HybridCpuManagedTypeDeclarationV1("Demo.ManagedException", HybridCpuManagedTypeKindV1.Class,
                "System.Exception", [], []));
        ulong exceptionBase = Type(exceptionTypes, "System.Exception").TypeId;
        ulong concreteException = Type(exceptionTypes, "Demo.ManagedException").TypeId;
        HybridCpuManagedEhClauseRegistrationV1 catchClause = new(
            HybridCpuManagedEhClauseKindV1.Catch, 0, 500, 500, 100, exceptionBase, 0);
        HybridCpuManagedExceptionRuntimeV1 exceptionRuntime = new HybridCpuManagedExceptionRuntimeV1(exceptionTypes,
            [EhRegistration("demo-eh", 1000, 1000, [catchClause])]);
        HybridCpuManagedExceptionDispatchResultV1 dispatch = exceptionRuntime.Dispatch(
            0x9000, concreteException, [new("demo-eh", 1010, 0x8000, 0)]);
        Require(dispatch.Status == HybridCpuManagedExceptionStatusV1.Handled &&
                dispatch.HandlerInstructionPointer == 1500 && !dispatch.UsedArchitecturalTrap,
            "managed exception runtime dispatch failed");
        rows.Add(("00", "ordinary CIL/CFG/SSA/phi", $"{scalar.Program!.Instructions.Count} canonical instructions"));
        rows.Add(("09", "managed EH metadata + dispatch", $"{eh.Plan.Clauses.Count} clauses; handler={dispatch.HandlerInstructionPointer}"));
        rows.Add(("15", "Roslyn async MoveNext EH admission", $"{asyncEh.Plan!.Clauses.Count} clause(s)"));
    }

    private static void DemonstrateHeapShapesAndGc(List<(string, string, string)> rows)
    {
        HybridCpuManagedTypeSystemV1 types = BuildTypes(
            new("Demo.Node", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("Value", HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0),
                 new("Next", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 1)]),
            new("Demo.Statics", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("Count", HybridCpuManagedStorageKindV1.Primitive, 4, 4, true, 0)]),
            new("System.Int32[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.Primitive, null, 4, 4, 16, 24, true, false)),
            new("System.String", HybridCpuManagedTypeKindV1.String, null, [], [], null,
                new(16, 20, 2, true)),
            new("System.Exception", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("_message", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0)]),
            new("Demo.TerminationException", HybridCpuManagedTypeKindV1.Class, "System.Exception", [], []),
            new("Demo.Pair", HybridCpuManagedTypeKindV1.ValueType, null, [], [], null, null,
                new(8, 8, [], 16)));
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, 1024 * 1024, 1024 * 1024, -3));
        Require(heap.Initialize().IsSuccess, "heap initialization failed");

        HybridCpuManagedTypeDescriptorV1 staticsType = Type(types, "Demo.Statics");
        ulong staticsHandle = Handle(types, "Demo.Statics");
        int countOffset = staticsType.StaticLayout.Fields.Single().OffsetBytes;
        HybridCpuManagedStaticFieldRuntimeV1 staticFields = new HybridCpuManagedStaticFieldRuntimeV1(types);
        HybridCpuManagedTypeInitializerRuntimeV1 initializers = new HybridCpuManagedTypeInitializerRuntimeV1(types);
        int initializerRuns = 0;
        Require(initializers.EnsureInitialized(staticsType.TypeId, () =>
        {
            initializerRuns++;
            return staticFields.StoreInt32(staticsHandle, countOffset, 42).IsSuccess;
        }).IsSuccess &&
        initializers.EnsureInitialized(staticsType.TypeId, () => { initializerRuns++; return false; }).IsSuccess &&
        initializerRuns == 1 && staticFields.LoadInt32(staticsHandle, countOffset).ScalarValue == 42,
            "runtime static initialization failed");

        ulong nodeHandle = Handle(types, "Demo.Node");
        ulong live = heap.Allocate(nodeHandle).ObjectReference;
        ulong garbage = heap.Allocate(nodeHandle).ObjectReference;
        ulong child = heap.Allocate(nodeHandle).ObjectReference;
        Require(live != 0 && garbage != 0 && live != garbage, "object allocation failed");
        Require(new HybridCpuManagedReferenceRuntimeV1().CheckNotNull(live, kernel).MayContinue,
            "managed reference null-check path failed");
        HybridCpuManagedTypeDescriptorV1 nodeType = Type(types, "Demo.Node");
        HybridCpuManagedFieldLayoutV1 valueField = nodeType.InstanceFields.Single(static field => field.Identity == "Value");
        HybridCpuManagedFieldLayoutV1 nextField = nodeType.InstanceFields.Single(static field => field.Identity == "Next");
        HybridCpuManagedFieldLoweringPlanV1 fieldPlan = new HybridCpuManagedFieldLoweringV1().Lower(
            nodeType, "Next", false, HybridCpuManagedFieldAccessKindV1.Store);
        Require(fieldPlan.Status == HybridCpuManagedFieldLoweringStatusV1.Lowered &&
                fieldPlan.Steps[0].Kind == HybridCpuManagedFieldLoweringStepKindV1.ExplicitNullCheckHelperCall &&
                heap.WriteObjectBytes(live, valueField.OffsetBytes, BitConverter.GetBytes(123)).IsSuccess &&
                heap.WriteObjectBytes(live, nextField.OffsetBytes, BitConverter.GetBytes(child)).IsSuccess,
            "instance field lowering/storage failed");

        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        ulong array = arrays.NewArray(Handle(types, "System.Int32[]"), 3).ObjectReference;
        Require(arrays.StoreInt32(array, 1, 17).IsSuccess && arrays.LoadInt32(array, 1).ScalarValue == 17,
            "SZARRAY round-trip failed");
        HybridCpuManagedStringRuntimeV1 strings = new HybridCpuManagedStringRuntimeV1(types, heap, Handle(types, "System.String"));
        ulong text = strings.MaterializeLiteral(Handle(types, "System.String"), "RefPlan7").ObjectReference;
        Require(strings.Length(text).ScalarValue == 8, "UTF-16 string length failed");
        ulong suffix = strings.MaterializeLiteral(Handle(types, "System.String"), " AOT").ObjectReference;
        ulong concatenated = strings.Concat3(text, 0, suffix).ObjectReference;
        Require(strings.Length(concatenated).ScalarValue == 12 &&
                strings.Character(concatenated, 8).ScalarValue == ' ',
            "bounded UTF-16 String.Concat(string,string,string) failed");
        ulong exception = heap.Allocate(Handle(types, "Demo.TerminationException")).ObjectReference;
        HybridCpuManagedExceptionObjectRuntimeV1 exceptionObjects = new HybridCpuManagedExceptionObjectRuntimeV1(types, heap,
            new(Type(types, "System.Exception").TypeId, "_message"));
        Require(exceptionObjects.SetMessage(exception, text).IsSuccess &&
                exceptionObjects.GetMessage(exception).ObjectReference == text,
            "bounded System.Exception message helper semantics failed");
        byte[] pair = BitConverter.GetBytes(0x0000_0007_0000_0003L);
        HybridCpuManagedValueTypeRuntimeV1 values = new HybridCpuManagedValueTypeRuntimeV1(types, heap);
        ulong box = values.Box(Handle(types, "Demo.Pair"), pair).ObjectReference;
        Require(values.Unbox(box, Type(types, "Demo.Pair").TypeId).SequenceEqual(pair), "boxing failed");

        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest,
            abi.TargetContractDigest, abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedNonMovingGcResultV1 collected = gc.Collect(new([], [],
            [new("live", HybridCpuManagedGcRootSourceV1.Handle, live),
             new("array", HybridCpuManagedGcRootSourceV1.Handle, array),
             new("string", HybridCpuManagedGcRootSourceV1.Handle, text),
             new("box", HybridCpuManagedGcRootSourceV1.Handle, box)]),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Require(collected.IsSuccess && collected.ReclaimedObjects.Contains(garbage) &&
                !collected.ReclaimedObjects.Contains(child), "non-moving transitive GC failed");
        rows.Add(("02/03", "object fields/layout/allocation", $"node={live:x}; Value=123; Next={child:x}"));
        Require(LanguageShowcase.StaticInitialization() == 42, "C# static initialization failed");
        rows.Add(("04", "static fields/type initialization", "runtime+C# value=42; initializer once"));
        rows.Add(("04", "arrays/strings/value boxing", "17 / RefPlan7 / Pair round-trips"));
        rows.Add(("09", "exception message helper", "exact field / derived exception round-trip"));
        rows.Add(("05", "precise non-moving GC", $"reclaimed={collected.ReclaimedObjects.Count}; moving={gc.IsMoving}"));
    }

    private static void DemonstrateDispatchDelegatesAndGenerics(List<(string, string, string)> rows)
    {
        const string signature = "object-ref,int32->int32";
        HybridCpuManagedTypeSystemV1 dispatchTypes = BuildTypes(
            new HybridCpuManagedTypeDeclarationV1("Demo.ITransform", HybridCpuManagedTypeKindV1.Interface, null, [], []),
            new HybridCpuManagedTypeDeclarationV1("Demo.CounterBase", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new HybridCpuManagedTypeDeclarationV1("Demo.ScalingCounter", HybridCpuManagedTypeKindV1.Class,
                "Demo.CounterBase", ["Demo.ITransform"], []));
        ulong interfaceTypeId = Type(dispatchTypes, "Demo.ITransform").TypeId;
        ulong baseTypeId = Type(dispatchTypes, "Demo.CounterBase").TypeId;
        ulong derivedTypeId = Type(dispatchTypes, "Demo.ScalingCounter").TypeId;
        ulong virtualSlot = HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(
            baseTypeId, "Demo.CounterBase.Add", signature);
        ulong interfaceSlot = HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(
            interfaceTypeId, "Demo.ITransform.Apply", signature);
        HybridCpuManagedDispatchTableBuildV1 dispatchTable = new HybridCpuManagedDispatchTableBuilderV1().Build(
            dispatchTypes,
            [new("Demo.ITransform.Apply", interfaceTypeId, signature, 0, 0, true, true),
             new("Demo.CounterBase.Add", baseTypeId, signature, 0x1000, 0, true, true),
             new("Demo.ScalingCounter.Add", derivedTypeId, signature, 0x2000, 0, true, false, virtualSlot)],
            [new(derivedTypeId, interfaceTypeId, interfaceSlot, "Demo.ScalingCounter.Add")]);
        Require(dispatchTable.IsSuccess, dispatchTable.Reason);
        DeterministicRuntimeKernelV1 dispatchKernel = BootKernel();
        HybridCpuManagedHeapAllocatorV1 dispatchHeap = new HybridCpuManagedHeapAllocatorV1(dispatchKernel, dispatchTypes,
            HybridCpuManagedHeapOptionsV1.Create(0x4200_0000, 4096, 4096, -3));
        Require(dispatchHeap.Initialize().IsSuccess, "dispatch heap initialization failed");
        ulong receiver = dispatchHeap.Allocate(Handle(dispatchTypes, "Demo.ScalingCounter")).ObjectReference;
        HybridCpuManagedDispatchRuntimeV1 dispatch = new HybridCpuManagedDispatchRuntimeV1(dispatchTypes, dispatchHeap, dispatchTable.Table!);
        Require(dispatch.ResolveVirtual(receiver, virtualSlot).CodeAddress == 0x2000 &&
                dispatch.ResolveInterface(receiver, interfaceTypeId, interfaceSlot).CodeAddress == 0x2000 &&
                dispatch.CastClass(receiver, baseTypeId).ObjectReference == receiver,
            "runtime virtual/interface/cast resolution failed");

        HybridCpuManagedFieldDeclarationV1[] delegateFields =
        [
            new("target", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0),
            new("code", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 1),
            new("context", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 2),
            new("signature", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 3),
            new("kind", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 4),
            new("thunk", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 5)
        ];
        HybridCpuManagedTypeSystemV1 delegateTypes = BuildTypes(
            new HybridCpuManagedTypeDeclarationV1("Demo.DelegateTarget", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new HybridCpuManagedTypeDeclarationV1("Demo.IntDelegate", HybridCpuManagedTypeKindV1.Class, null, [], delegateFields));
        DeterministicRuntimeKernelV1 delegateKernel = BootKernel();
        HybridCpuManagedHeapAllocatorV1 delegateHeap = new HybridCpuManagedHeapAllocatorV1(delegateKernel, delegateTypes,
            HybridCpuManagedHeapOptionsV1.Create(0x4300_0000, 4096, 4096, -3));
        Require(delegateHeap.Initialize().IsSuccess, "delegate heap initialization failed");
        ulong delegateTarget = delegateHeap.Allocate(Handle(delegateTypes, "Demo.DelegateTarget")).ObjectReference;
        ulong delegateSignature = HybridCpuManagedDelegateRuntimeV1.ComputeSignatureId("int32(int32)");
        HybridCpuManagedDelegateRuntimeV1 delegates = new HybridCpuManagedDelegateRuntimeV1(delegateTypes, delegateHeap,
            [new(1, delegateSignature, 0x110000, false, 0x130000),
             new(2, delegateSignature, 0x120000, true, 0x130000)]);
        HybridCpuManagedDelegateResultV1 closed = delegates.Create(new(
            Handle(delegateTypes, "Demo.IntDelegate"), delegateSignature,
            HybridCpuManagedDelegateKindV1.ClosedInstance, 0x120000, delegateTarget));
        Require(closed.IsSuccess && delegates.ResolveInvocation(closed.DelegateReference, delegateSignature).TargetObject == delegateTarget,
            "closed-instance delegate resolution failed");

        int virtualResult = LanguageShowcase.VirtualInterfaceDispatch(new LanguageShowcase.Doubler(), 21);
        int delegateResult = LanguageShowcase.ArraysStringsValuesAndDelegates();
        int genericResult = LanguageShowcase.GenericIdentity(37);
        int instanceResult = LanguageShowcase.InstanceObjectModel();
        Require(virtualResult == 42 && delegateResult == 23 && genericResult == 37 &&
                instanceResult == 55 && LanguageShowcase.GenericInstanceObject() == "42",
            "language feature corpus failed");
        rows.Add(("06", "instance/virtual/interface/cast", $"runtime=0x2000; C# object result={instanceResult}"));
        rows.Add(("07", "closed-instance delegates/pointers", $"target={delegateTarget:x}; closure={delegateResult}"));
        rows.Add(("08", "exact generic methods/types", $"Identity<Int32>={genericResult}; Box<Pair>=42"));
        bool finallyRan;
        Require(LanguageShowcase.ExceptionAndFinally(true, out finallyRan) == -7 && finallyRan,
            "managed exception/finally behavior failed");
        rows.Add(("09", "managed exception behavior", "catch=-7; finally=true"));
    }

    private static void DemonstrateKernelThreadsTimersAndSynchronization(List<(string, string, string)> rows)
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedTlsLayoutV1 tls = HybridCpuManagedTlsLayoutV1.Create([
            new("demo.counter", false)
        ]);
        HybridCpuManagedThreadingRuntimeV1 threads = new HybridCpuManagedThreadingRuntimeV1(kernel, tls,
            HybridCpuManagedThreadingOptionsV1.Qualification);
        Require(threads.AttachInitialThread("main").IsSuccess, "initial managed thread attach failed");
        HybridCpuManagedThreadResultV1 worker = threads.CreateThread("worker",
            new(0x0010_0000, 0x5000_1000, 4096, 0x5000_0000, 4096, 0x6000_0000, 0x6100_0000, 1));
        Require(worker.IsSuccess && threads.Start(worker.Thread!.ManagedThreadId).IsSuccess, "worker start failed");
        HybridCpuManagedThreadSnapshotV1 mainThread = threads.Threads().Single(static item => item.Name == "main");
        HybridCpuManagedThreadSnapshotV1 workerThread = threads.Threads().Single(static item => item.Name == "worker");
        Require(threads.WriteTls(mainThread.ManagedThreadId, "demo.counter", 11).IsSuccess &&
                threads.WriteTls(workerThread.ManagedThreadId, "demo.counter", 29).IsSuccess &&
                threads.TryReadTls(mainThread.ManagedThreadId, "demo.counter", out ulong mainTls) && mainTls == 11 &&
                threads.TryReadTls(workerThread.ManagedThreadId, "demo.counter", out ulong workerTls) && workerTls == 29,
            "per-managed-thread TLS isolation failed");

        HybridCpuExecutionContextDescriptorV1 trapContext = kernel.CurrentContext()
            ?? throw new InvalidOperationException("trap demo has no current execution context");
        HybridCpuCommittedRegisterStateV1 committed = HybridCpuTrapAbiV1.CreateCommittedState(
            trapContext.EntryAddress, trapContext.InitialStackPointer,
            Enumerable.Range(0, 32).Select(static value => (ulong)value));
        HybridCpuArchitecturalTrapRecordV1 trap = HybridCpuTrapAbiV1.CreateRecord(
            trapContext.ContextId, 1,
            new(trapContext.VirtualThreadCarrier, trapContext.EntryAddress, trapContext.InitialStackPointer, 13, 0),
            HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault, 1,
            HybridCpuMemoryAccessKindV1.Read, HybridCpuPrivilegeModeV1.User,
            HybridCpuExecutionModeV1.ManagedUser, true, HybridCpuFaultAddressSemanticV1.VirtualAddress,
            committed, false, true, HybridCpuTrapResumePolicyV1.ManagedDispatch);
        HybridCpuKernelTrapResultV1 trapEntry = kernel.TrapEntry(trap);
        HybridCpuManagedTypeSystemV1 trapTypes = BuildTypes(
            new HybridCpuManagedTypeDeclarationV1("System.Exception", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new HybridCpuManagedTypeDeclarationV1("System.NullReferenceException", HybridCpuManagedTypeKindV1.Class,
                "System.Exception", [], []));
        HybridCpuManagedHeapAllocatorV1 trapHeap = new HybridCpuManagedHeapAllocatorV1(kernel, trapTypes,
            HybridCpuManagedHeapOptionsV1.Create(0x4500_0000, 4096, 4096, -3));
        Require(trapHeap.Initialize().IsSuccess, "trap exception heap initialization failed");
        HybridCpuManagedTrapMapperV1 trapMapper = new HybridCpuManagedTrapMapperV1(trapTypes, HybridCpuManagedTrapMappingOptionsV1.Qualification);
        HybridCpuManagedTrapMappingResultV1 mapping = trapMapper.Map(trap, trapEntry,
            type => trapHeap.Allocate(trapTypes.TypeHandle(type.TypeId)!.Value).ObjectReference);
        Require(mapping.IsMapped && mapping.ExceptionTypeIdentity == "System.NullReferenceException" &&
                kernel.TrapReturn(trap).IsSuccess,
            "architectural fault to managed exception mapping failed");

        HybridCpuManagedTypeSystemV1 gcTypes = BuildTypes(
            new HybridCpuManagedTypeDeclarationV1("Demo.ThreadRoot", HybridCpuManagedTypeKindV1.Class, null, [], []));
        HybridCpuManagedHeapAllocatorV1 gcHeap = new HybridCpuManagedHeapAllocatorV1(kernel, gcTypes,
            HybridCpuManagedHeapOptionsV1.Create(0x4400_0000, 4096, 4096, -3));
        Require(gcHeap.Initialize().IsSuccess, "multi-thread GC heap initialization failed");
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 collector = new HybridCpuManagedNonMovingGcV1(gcTypes, gcHeap, abi.ContractDigest,
            abi.TargetContractDigest, abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedMultiThreadGcResultV1 rendezvous = threads.CollectAllThreads(
            mainThread.ManagedThreadId, collector, [], []);
        Require(rendezvous.IsSuccess, rendezvous.Reason);

        ulong mainContext = kernel.CurrentContext()!.ContextId;
        Require(kernel.SleepUntil(new(mainContext, 10, 1)).IsSuccess, "sleep_until failed");
        Require(kernel.AdvanceMonotonicTime(10).CompletedTokens.SequenceEqual([1UL]), "virtual deadline failed");

        HybridCpuManagedSynchronizationPlanV1 volatileRead = HybridCpuManagedSynchronizationLoweringV1.Plan(
            HybridCpuManagedMemoryOperationV1.VolatileRead, 64, 8);
        HybridCpuManagedSynchronizationPlanV1 atomicAdd = HybridCpuManagedSynchronizationLoweringV1.Plan(
            HybridCpuManagedMemoryOperationV1.InterlockedAdd, 32, 4);
        Require(volatileRead.IsSupported && atomicAdd.IsSupported, "synchronization lowering failed");
        HybridCpuManagedMemoryModelV1 memory = new HybridCpuManagedMemoryModelV1(HybridCpuManagedSynchronizationOptionsV1.Qualification);
        memory.Seed(0x1000, 0); memory.Seed(0x2000, 0);
        Require(memory.OrdinaryStore(mainThread.ExecutionContextId, 0x1000, 42).IsSuccess &&
                memory.VolatileWrite(mainThread.ExecutionContextId, 0x2000, 1).IsSuccess &&
                memory.VolatileRead(workerThread.ExecutionContextId, 0x2000).ObservedValue == 1 &&
                memory.OrdinaryLoad(workerThread.ExecutionContextId, 0x1000).ObservedValue == 42 &&
                memory.CompareExchange(workerThread.ExecutionContextId, 0x1000, 43, 42).ValueChanged,
            "runtime memory model failed");
        HybridCpuManagedMonitorRuntimeV1 monitor = new HybridCpuManagedMonitorRuntimeV1(threads, kernel,
            HybridCpuManagedSynchronizationOptionsV1.Qualification);
        Require(monitor.Enter(mainThread.ManagedThreadId, 0x8000).Disposition == HybridCpuManagedMonitorDispositionV1.Acquired &&
                monitor.Enter(mainThread.ManagedThreadId, 0x8000).Disposition == HybridCpuManagedMonitorDispositionV1.Recursive &&
                monitor.Exit(mainThread.ManagedThreadId, 0x8000).Disposition == HybridCpuManagedMonitorDispositionV1.Released &&
                monitor.Exit(mainThread.ManagedThreadId, 0x8000).Disposition == HybridCpuManagedMonitorDispositionV1.Released,
            "runtime Monitor recursion failed");
        Require(LanguageShowcase.ThreadingTlsAndSynchronization() == 7, "host synchronization corpus failed");
        rows.Add(("10", "precise trap-to-managed mapping", $"{mapping.ExceptionTypeIdentity}; TrapAbi {Short(HybridCpuTrapAbiV1.ContractDigest)}"));
        rows.Add(("11", "threads/TLS/multi-thread GC", $"threads={threads.Threads().Count}; TLS=11/29; rendezvous=success"));
        rows.Add(("12", "memory model/atomics/Monitor", $"value={memory.CommittedValue(0x1000)}; lowering={volatileRead.SuccessPath.Count}+{atomicAdd.SuccessPath.Count}"));
        rows.Add(("15", "virtual monotonic timer", $"ticks={kernel.MonotonicTicks()}"));
    }

    private static void DemonstrateInteropAndReflection(List<(string, string, string)> rows)
    {
        HybridCpuManagedInteropSignatureV1 signature = new HybridCpuManagedInteropSignatureV1("hc.demo", "add_i8",
            HybridCpuInteropCallingConventionV1.HybridCpuNativeV2,
            [new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8), new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8)],
            new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8));
        HybridCpuManagedInteropThunkPlanV1 thunk = HybridCpuManagedInteropLoweringV1.Plan(signature);
        Require(thunk.IsSupported, thunk.Reason);
        DeterministicMockHostServiceProviderV1 provider = new DeterministicMockHostServiceProviderV1();
        Require(provider.Register(HybridCpuHostServiceV1.Console, 1,
            new(HybridCpuExternalServiceStatusV1.Success, 18, 0, "deterministic add_i8")),
            "host service registration failed");
        DeterministicRuntimeKernelV1 interopKernel = BootKernel(provider);
        HybridCpuManagedThreadingRuntimeV1 interopThreads = new HybridCpuManagedThreadingRuntimeV1(interopKernel,
            HybridCpuManagedTlsLayoutV1.Create([]), HybridCpuManagedThreadingOptionsV1.Qualification);
        Require(interopThreads.AttachInitialThread("interop-main").IsSuccess, "interop managed thread attach failed");
        ulong interopThreadId = interopThreads.Threads().Single().ManagedThreadId;
        HybridCpuManagedNativeResolverV1 resolver = new HybridCpuManagedNativeResolverV1();
        Require(resolver.Register(signature, HybridCpuHostServiceV1.Console, 1), "native symbol registration failed");
        HybridCpuManagedInteropRuntimeV1 interop = new HybridCpuManagedInteropRuntimeV1(interopKernel, interopThreads, resolver,
            HybridCpuManagedInteropOptionsV1.Qualification);
        HybridCpuManagedInteropResultV1 invocation = interop.Invoke(new(interopThreadId, signature, [7, 11],
            PinnedObjectReferences: [0x7000]));
        Require(invocation.IsSuccess && invocation.ReturnValue == 18 && invocation.NativeCallRoots.SequenceEqual([0x7000UL]) &&
                interop.NativeCallRoots().Count == 0,
            "managed/native host-service transition failed");

        HybridCpuManagedTypeSystemV1 types = BuildTypes(
            new HybridCpuManagedTypeDeclarationV1("Demo.Widget", HybridCpuManagedTypeKindV1.Class, null, [], []));
        HybridCpuManagedReflectionRetentionResultV1 retained = new HybridCpuManagedReflectionMetadataBuilderV1().Build(
            types.Descriptors,
            [new("Demo.Widget", "Demo.Widget.Value", "Value", HybridCpuManagedReflectionMemberKindV1.Property,
                "System.Int32", true, false, 0)],
            [new("Demo.Widget", ["Demo.Widget.Value"])],
            HybridCpuManagedReflectionRetentionOptionsV1.Qualification);
        Require(retained.IsSuccess, retained.Reason);
        HybridCpuManagedReflectionRuntimeV1 reflection = new HybridCpuManagedReflectionRuntimeV1(retained.Table!,
            HybridCpuManagedReflectionOptionsV1.Qualification);
        HybridCpuManagedReflectionResultV1 reflected = reflection.GetType("Demo.Widget");
        Require(reflected.IsSuccess && reflection.GetMembers(reflected.Type!, "Value").IsSuccess,
            "bounded reflection lookup failed");
        Require(LanguageShowcase.BoundedReflection() == "Left", "host reflection corpus failed");
        rows.Add(("13", "P/Invoke thunk + host transition", $"7+11={invocation.ReturnValue}; steps={thunk.Steps.Count}"));
        rows.Add(("14", "bounded retained reflection", $"types={retained.Table!.Types.Count}; members=1"));
    }

    private static async Task DemonstrateAsync(List<(string, string, string)> rows)
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedAsyncRuntimeV1 runtime = new HybridCpuManagedAsyncRuntimeV1(kernel, HybridCpuManagedAsyncOptionsV1.Qualification);
        ulong source = RequireTask(runtime.CreateTask(0x1000));
        ulong target = RequireTask(runtime.CreateTask(0x2000));
        Require(runtime.ContinueWith(source, target, 0x3000, 0x4000, "demo-context").IsSuccess,
            "continuation enqueue failed");
        Require(runtime.Complete(source, 41).IsSuccess, "task completion failed");
        HybridCpuManagedAsyncResultV1 completed = runtime.RunNext(0, _ =>
            new(HybridCpuManagedTaskStatusV1.Succeeded, 42));
        Require(completed.IsSuccess && completed.Task!.ResultValue == 42, "cooperative continuation failed");
        HybridCpuManagedTaskSnapshotV1 completedTask = completed.Task
            ?? throw new InvalidOperationException("completed continuation did not return its task snapshot");
        Require(await LanguageShowcase.AsyncAndCancellationAsync() == 42, "C# async corpus failed");
        ulong cancelledTask = RequireTask(runtime.CreateTask(0x5000));
        Require(runtime.Cancel(cancelledTask).IsSuccess, "runtime task cancellation failed");
        ulong faultedTask = RequireTask(runtime.CreateTask(0x6000));
        Require(runtime.Fault(faultedTask, "Demo.ManagedException").Task?.Status == HybridCpuManagedTaskStatusV1.Faulted,
            "runtime task fault failed");
        ulong delayedTask = RequireTask(runtime.CreateTask(0x7000));
        ulong asyncContext = kernel.CurrentContext()?.ContextId ?? 0;
        Require(runtime.Delay(delayedTask, asyncContext, 5).IsSuccess &&
                runtime.AdvanceVirtualTime(5).IsSuccess &&
                runtime.Tasks().Single(task => task.TaskId == delayedTask).Status == HybridCpuManagedTaskStatusV1.Succeeded,
            "runtime virtual delay failed");
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancellationObserved = false;
        try { _ = await LanguageShowcase.AsyncAndCancellationAsync(cancellation.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Require(cancellationObserved, "C# async cancellation failed");
        rows.Add(("15", "Task/async/delay/fault/cancel", $"result={completedTask.ResultValue}; tasks={runtime.Tasks().Count}"));
    }

    private static void DemonstrateFinalReleaseManifest(List<(string, string, string)> rows)
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "HybridCPU_Compiler", "RefPlan7", "evidence",
            "2026-08-29-phase16-final-release-5f1657c", "refplan7-final-release-manifest-v1.json");
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = JsonSerializer.Deserialize<HybridCpuRefPlan7FinalReleaseManifestV1>(
            File.ReadAllText(path)) ?? throw new InvalidDataException("Phase 16 manifest is empty.");
        HybridCpuRefPlan7FinalReleaseValidationV1 validation = HybridCpuRefPlan7FinalReleaseV1.Validate(manifest);
        Require(validation.IsValid, $"{validation.Code}: {validation.Reason}");
        Require(manifest.EnabledFeatureBits.Count == 0 && manifest.EvidenceArtifacts.Count == 16,
            "final release disposition drifted");
        rows.Add(("16", "final evidence-bound release", $"16 manifests; image {Short(manifest.ImageQualification.BuildASha256)}"));
    }

    private static DeterministicRuntimeKernelV1 BootKernel(IHybridCpuHostServiceProviderV1? provider = null)
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1(provider);
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,
            new string('a', 64), 0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0));
        Require(boot.IsSuccess, boot.Reason);
        return kernel;
    }

    private static HybridCpuManagedTypeSystemV1 BuildTypes(params HybridCpuManagedTypeDeclarationV1[] declarations)
    {
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build(declarations);
        Require(built.IsSuccess, built.Reason);
        return built.TypeSystem!;
    }

    private static HybridCpuManagedTypeDescriptorV1 Type(HybridCpuManagedTypeSystemV1 types, string identity) =>
        types.Descriptors.Single(row => row.StableIdentity == identity);
    private static ulong Handle(HybridCpuManagedTypeSystemV1 types, string identity) =>
        types.TypeHandle(Type(types, identity).TypeId)!.Value;
    private static ulong RequireTask(HybridCpuManagedAsyncResultV1 result)
    {
        Require(result.IsSuccess, result.Reason);
        return result.Task!.TaskId;
    }
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static string Short(string digest) => digest[..12];
    private static HybridCpuManagedEhMethodRegistrationV1 EhRegistration(string method, int start, int size,
        IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> clauses) =>
        new(method, start, size, EncodeEh(clauses), HybridCpuManagedUnwindCodecV2.Encode(new(
            HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer, 0,
            HybridCpuNativeAbiContractV2.ReturnAddressRegister, null, [])));

    private static byte[] EncodeEh(IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> clauses)
    {
        byte[] bytes = new byte[12 + clauses.Count * 40];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), clauses.Count);
        for (int index = 0, offset = 12; index < clauses.Count; index++, offset += 40)
        {
            HybridCpuManagedEhClauseRegistrationV1 row = clauses[index];
            bytes[offset] = (byte)row.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), row.TryStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), row.TrySizeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), row.HandlerStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 16), row.HandlerSizeBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 20), row.CatchTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 28), row.Ordinal);
        }
        return bytes;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "HybridCPU_Compiler")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("HybridCPU repository root not found.");
    }
}
