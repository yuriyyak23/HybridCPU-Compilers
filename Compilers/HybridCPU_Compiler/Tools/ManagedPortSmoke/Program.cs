using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static string Diagnostics(RestrictedCilImportResultV1 result) =>
    string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
static RestrictedCilMethodSelectorV1 Select(string name) => new(typeof(Fixture).FullName!, name);

if (args is ["--list-eh", string listEhAssemblyPath])
{
    byte[] listEhImage = File.ReadAllBytes(listEhAssemblyPath);
    using MemoryStream listEhStream = new MemoryStream(listEhImage, writable: false);
    using PEReader peReader = new PEReader(listEhStream);
    MetadataReader listEhMetadata = peReader.GetMetadataReader();
    foreach (TypeDefinitionHandle typeHandle in listEhMetadata.TypeDefinitions)
    {
        TypeDefinition type = listEhMetadata.GetTypeDefinition(typeHandle);
        string ns = listEhMetadata.GetString(type.Namespace);
        string listEhTypeName = (ns.Length == 0 ? string.Empty : ns + ".") + listEhMetadata.GetString(type.Name);
        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
        {
            MethodDefinition method = listEhMetadata.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            MethodBodyBlock body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            if (body.ExceptionRegions.Length == 0) continue;
            string listEhMethodName = listEhMetadata.GetString(method.Name);
            ManagedEhImportResultV1 plan = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
                .ImportManagedEhPlan(listEhImage, new(listEhTypeName, listEhMethodName));
            Console.WriteLine($"{listEhTypeName}::{listEhMethodName}: regions={body.ExceptionRegions.Length}; status={plan.Status}; " +
                $"clauses=[{string.Join(',', plan.Plan?.Clauses.Select(clause => $"{clause.Kind}:IL_{clause.TryOffset:x4}+{clause.TryLength}->IL_{clause.HandlerOffset:x4}+{clause.HandlerLength}") ?? [])}]; " +
                $"ops=[{string.Join(',', plan.Plan?.Operations.Select(operation => $"{operation.Kind}:IL_{operation.IlOffset:x4}") ?? [])}]");
        }
    }
    return;
}

if (args is ["--eh-plan", string assemblyPath, string typeName, string methodName])
{
    ManagedEhImportResultV1 plan = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
        .ImportManagedEhPlan(File.ReadAllBytes(assemblyPath), new(typeName, methodName));
    Console.WriteLine($"{plan.Status}: {plan.Code} {plan.Reason}");
    if (plan.Plan is not null)
    {
        Console.WriteLine($"Clauses={plan.Plan.Clauses.Count}; operations={plan.Plan.Operations.Count}; digest={plan.Plan.ContractDigest}");
        if (plan.ControlFlow is { } ehGraph)
            Console.WriteLine($"EH CFG: instructions={ehGraph.InstructionOffsets.Count}; edges={ehGraph.Edges.Count}; homes=[{string.Join(',', ehGraph.RequiredStateHomes)}]; digest={ehGraph.Digest}");
        foreach (ManagedEhHandlerEntryV1 entry in plan.Plan.HandlerEntries)
            Console.WriteLine($"handler IL_{entry.IlOffset:x4}: stack=[{string.Join(',', entry.EvaluationStack)}], exception-register={entry.ExceptionReferenceRegister}");
        foreach (ManagedEhStateHomeV1 home in plan.ControlFlow?.StateHomes ?? [])
            Console.WriteLine($"home {home.Slot}: type={home.Type}, object-root={home.IsObjectRoot}, initialized-at-entry={home.InitializedAtMethodEntry}");
        foreach (ManagedEhLeaveTransferV1 transfer in plan.Plan.LeaveTransfers)
            Console.WriteLine($"leave IL_{transfer.IlOffset:x4}->IL_{transfer.TargetOffset:x4}: {string.Join(',', transfer.Actions.Select(action => $"{action.Kind}:{action.ClauseOrdinal}"))}");
    }
    Environment.ExitCode = plan.Status == ManagedEhImportStatusV1.Success ? 0 : 1;
    return;
}

if (args is ["--branch-entry-next-gate"]) { BranchEntrySmoke.ReportNextGate(); return; }
if (args is ["--branch-entry"]) { BranchEntrySmoke.Run(); return; }
if (args is ["--nullable-values"]) { NullableValueSmoke.Run(); return; }
if (args is ["--nullable-sequential"]) { NullableValueSmoke.RunSequential(); return; }
if (args is ["--nullable-nested"]) { NullableValueSmoke.RunNested(); return; }
if (args is ["--nullable-dependent"]) { NullableValueSmoke.RunDependent(); return; }
if (args is ["--nullable-affine"]) { NullableValueSmoke.RunAffine(); return; }
if (args is ["--nullable-scaled"]) { NullableValueSmoke.RunScaled(); return; }
if (args is ["--nullable-return"]) { NullableValueSmoke.RunReturnWords(); return; }
if (args is ["--nullable-binary-return"]) { NullableValueSmoke.RunBinaryReturnWords(); return; }
if (args is ["--nullable-mixed-return"]) { NullableValueSmoke.RunMixedReturnWords(); return; }
if (args is ["--nullable-return-helper"]) { NullableValueSmoke.RunReturnHelperWords(); return; }
if (args is ["--nullable-map"]) { NullableValueSmoke.RunMapWords(); return; }
if (args is ["--nullable-return-composition"]) { NullableValueSmoke.RunReturnHelperWords(true); NullableValueSmoke.RunMixedReturnWords(true); return; }
if (args is ["--unsigned-division"]) { UnsignedDivisionSmoke.Run(); return; }
if (args is ["--throw-boundary"]) { ThrowBoundarySmoke.Run(); return; }
if (args is ["--malformed-ingress"]) { MalformedIngressSmoke.Run(); return; }
if (args is ["--eh-ingress-budget"]) { EhIngressBudgetSmoke.Run(); return; }
if (args is ["--generic-ingress"]) { GenericIngressSmoke.Run(); return; }
if (args is ["--ingress-cancellation"]) { IngressCancellationSmoke.Run(); return; }
if (args is ["--canonical-cancellation"]) { CanonicalCancellationSmoke.Run(); return; }
if (args is ["--bundler-cancellation"]) { BundlerCancellationSmoke.Run(); return; }
if (args is ["--postbundle-cancellation"]) { PostBundleCancellationSmoke.Run(); return; }
if (args is ["--calli-header"]) { CalliHeaderSmoke.Run(); return; }
if (args is ["--method-header"]) { MethodHeaderSmoke.Run(); return; }
if (args is ["--fnptr-header"]) { FunctionPointerHeaderSmoke.Run(); return; }
if (args is ["--local-count"]) { LocalSignatureCountSmoke.Run(); return; }
if (args is ["--nested-generic-count"]) { NestedGenericCountSmoke.Run(); return; }
if (args is ["--typespec-count"]) { TypeSpecCountSmoke.Run(); return; }
if (args is ["--module-acquisition"]) { ModuleAcquisitionSmoke.Run(); return; }
if (args is ["--serializer-cancellation"]) { SerializerCancellationSmoke.Run(); return; }
if (args is ["--allocation-orchestration"]) { AllocationOrchestrationSmoke.Run(); return; }
if (args is ["--allocator-cancellation"]) { AllocatorCancellationSmoke.Run(); return; }
if (args is ["--managed-link-cancellation"]) { ManagedLinkCancellationSmoke.Run(); return; }
if (args is ["--pcl-admission"]) { PclAdmissionSmoke.Run(); return; }
if (args is ["--nativeaot-compile-cancellation"]) { NativeAotCompileCancellationSmoke.Run(); return; }
if (args is ["--native-constants"]) { NativeConstantSmoke.Run(); return; }
if (args is ["--closure-identity"]) { ManagedClosureIdentitySmoke.Run(); return; }
if (args is ["--source-map"]) { SourceMapSmoke.Run(); return; }
if (args.Length == 0) BranchEntrySmoke.Run();
if (args is ["--allocation-capacity"]) { AllocationCapacitySmoke.Run(); return; }
if (args is ["--receiver-abi"]) { ReceiverAbiSmoke.Run(null); return; }
if (args is ["--static-projection"]) { StaticProjectionSmoke.Run(); return; }
if (args is ["--closed-interface"]) { ClosedInterfaceSmoke.Run(); return; }
if (args is ["--metadata-string-literal"]) { MetadataStringLiteralSmoke.Run(); return; }
if (args is ["--allocation-type-closure"]) { AllocationTypeClosureSmoke.Run(); return; }
SchedulerSuccessorSmoke.Run();
if (args is ["--scheduler-successors"]) return;
AllocationCapacitySmoke.Run();

ScalarControlFlowV2Budgets profileBudgets = ScalarControlFlowV2ProfileContractV1.Default.Budgets;
Check(profileBudgets.IsValid && profileBudgets.MaximumReachableMethods == 4096 &&
      profileBudgets.MaximumIlInstructionsPerMethod == 16384 &&
      profileBudgets.MaximumOutgoingCallsPerMethod == 4096 &&
      profileBudgets.MaximumCallEdges == 65536 &&
      profileBudgets.MaximumIlInstructionsProgram == 4 * 1024 * 1024 &&
      profileBudgets.MaximumCodeBytes == 128 * 1024 * 1024,
    "production graph budgets must remain bounded and internally consistent");
Check(RestrictedCilImportBudgetsV1.Production.MaximumDecodedInstructions == 16384 &&
      RestrictedCilImportBudgetsV1.Production.MaximumCalls == 4096,
    "single-method and graph decoder limits must remain aligned");

byte[] pe = File.ReadAllBytes(typeof(Fixture).Assembly.Location);
HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
    new("System.Byte[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
        new(HybridCpuManagedStorageKindV1.Primitive, null, 1, 1, 16, 24, true, false)),
    new("System.Int32[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
        new(HybridCpuManagedStorageKindV1.Primitive, null, 4, 4, 16, 24, true, false))]);
Check(built.IsSuccess, built.Reason);
HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
HybridCpuManagedTypeDescriptorV1 bytesType = types.Descriptors.Single(t => t.StableIdentity == "System.Byte[]");
ulong bytesHandle = types.TypeHandle(bytesType.TypeId)!.Value;
using MemoryStream stream = new MemoryStream(pe, false);
using PEReader reader = new PEReader(stream);
MetadataReader metadata = reader.GetMetadataReader();
MethodInfo blobMethod = typeof(Fixture).GetMethod(nameof(Fixture.Blob))!;
byte[] il = blobMethod.GetMethodBody()!.GetILAsByteArray()!;
int newArrayOffset = Array.IndexOf(il, (byte)0x8d);
int fieldOffset = Array.IndexOf(il, (byte)0xd0);
Check(newArrayOffset >= 0 && fieldOffset >= 0, "Fixture must contain newarr + ldtoken FieldRVA.");
int elementToken = BitConverter.ToInt32(il, newArrayOffset + 1);
int fieldToken = BitConverter.ToInt32(il, fieldOffset + 1);
FieldDefinition field = metadata.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(fieldToken));
byte[] data = reader.GetSectionData(field.GetRelativeVirtualAddress()).GetContent(0, 8).ToArray();
RestrictedCilArrayTypeBindingV1 arrayBinding = new RestrictedCilArrayTypeBindingV1(elementToken, bytesType, bytesHandle);
RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
    arrayBindings: [arrayBinding], fieldDataBindings: [new(fieldToken, 1, data)]);
foreach (string name in new[] { nameof(Fixture.PassStructArray), nameof(Fixture.Empty), nameof(Fixture.Blob) })
{
    RestrictedCilImportResultV1 result = importer.ImportImage(pe, Select(name));
    Check(result.Status == RestrictedCilImportStatusV1.Success, name + ": " + Diagnostics(result));
    Console.WriteLine("PASS import " + name);
}
RestrictedCilImportResultV1 missing = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
    .ImportImage(pe, Select(nameof(Fixture.Empty)));
Check(missing.Diagnostics.Any(d => d.Code == "HCCIL1462"), "Empty without exact binding must fail closed.");
RestrictedCilImportResultV1 unsupported = importer.ImportImage(pe, Select(nameof(Fixture.OtherGeneric)));
Check(unsupported.Status != RestrictedCilImportStatusV1.Success, "Arbitrary MethodSpec must remain closed.");
RestrictedCilImportResultV1 tampered = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
    arrayBindings: [arrayBinding], fieldDataBindings: [new(fieldToken, 1, new byte[8])])
    .ImportImage(pe, Select(nameof(Fixture.Blob)));
Check(tampered.Diagnostics.Any(d => d.Code == "HCCIL1463"), "FieldRVA data cannot be substituted.");
ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
    pe, "managed-port-smoke", [Select(nameof(Fixture.Blob))], []));
Check(graph.Status == RestrictedCilImportStatusV1.Success,
    string.Join("; ", graph.Diagnostics.Select(d => d.Code + ": " + d.Message)));
Check(graph.TypeUniverse is { Rows.Count: > 0, PlanDigest: { Length: 64 } } &&
      graph.TypeUniverse.Rows.Select(static row => row.TypeHandle).SequenceEqual(
          Enumerable.Range(1, graph.TypeUniverse.Rows.Count).Select(static value => (ulong)value)) &&
      graph.TypeUniverse.Rows.Select(static row => row.TypeId).SequenceEqual(
          graph.TypeUniverse.Rows.Select(static row => row.TypeId).Order()),
    "graph must retain its exact ordinal-handle/type/base universe for image-native type tests");
ProfileClosureSmoke.Run(graph);
Console.WriteLine("PASS graph FieldRVA extraction and InitializeArray helper resolution");

DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
    0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "kernel boot");
HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
    HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
Check(heap.Initialize().IsSuccess, "heap init");
HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
Check(arrays.RegisterFieldData(1, data), "register data");
data[0] = 99;
ulong array = arrays.NewArray(bytesHandle, 8).ObjectReference;
Check(arrays.InitializeArray(array, 1).IsSuccess, "initialize array");
Check(heap.ReadObjectBytes(array)!.AsSpan(24, 8).SequenceEqual(Fixture.Blob()), "immutable copied FieldRVA bytes");
byte[] before = heap.ReadObjectBytes(array)!;
Check(arrays.RegisterFieldData(2, new byte[7]), "short blob register");
Check(!arrays.InitializeArray(array, 2).IsSuccess && before.SequenceEqual(heap.ReadObjectBytes(array)!),
    "size mismatch must leave target unchanged");
Check(!arrays.InitializeArray(array, 0).IsSuccess, "unknown handle must fail");
Check(!arrays.InitializeArray(0, 1).IsSuccess, "null array must fail");
Console.WriteLine("PASS immutable FieldRVA runtime copy and fail-closed validation");

ulong empty = arrays.Empty(bytesHandle).ObjectReference;
Check(empty != 0 && arrays.Empty(bytesHandle).ObjectReference == empty && arrays.Length(empty).ScalarValue == 0,
    "Array.Empty identity and length");
ulong intsHandle = types.TypeHandle(types.Descriptors.Single(t => t.StableIdentity == "System.Int32[]").TypeId)!.Value;
Check(arrays.Empty(intsHandle).ObjectReference != empty, "closed types need distinct empty singletons");
HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest, abi.TargetContractDigest,
    abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
HybridCpuManagedNonMovingGcResultV1 collected = gc.Collect(new([], [], [], Arrays: arrays), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
Check(collected.IsSuccess && collected.Roots.Contains(empty) && !collected.ReclaimedObjects.Contains(empty) &&
    collected.ReclaimedObjects.Contains(array), collected.Reason);
Check(arrays.Empty(bytesHandle).ObjectReference == empty, "empty singleton survives collection");
Console.WriteLine("PASS closed Array.Empty singleton identity and GC roots");
ArgumentNullSmoke.Run();
InlineStorageSmoke.Run();
ValueArraySmoke.Run();
AggregateFlowSmoke.Run();
AggregateCallSmoke.Run();
EmptyValueArraySmoke.Run();
ArrayZeroSmoke.Run();
ByteArraySmoke.Run();
ThrowBoundarySmoke.Run();
EhDispatchSmoke.Run();
EhStateSmoke.Run();
EhUnwindSmoke.Run();
EhNativeTransferSmoke.Run();
EhFinallyContinuationSmoke.Run();
EhClauseSelectorSmoke.Run();
EhPlanSmoke.Run();
ArrayCopySmoke.Run();
IsInstanceSmoke.Run();
MathAbsSmoke.Run();
Int16ArraySmoke.Run();
StaticProjectionSmoke.Run();
TypeInitializerCallGraphSmoke.Run();
CycleWitnessCallGraphSmoke.Run();
UnsignedDivisionSmoke.Run();
NegationSmoke.Run();
ClosedInterfaceSmoke.Run();
VirtualClockSmoke.Run();
InterfaceResolverSmoke.Run();
MetadataInputSmoke.Run();
MalformedIngressSmoke.Run();
EhIngressBudgetSmoke.Run();
GenericIngressSmoke.Run();
IngressCancellationSmoke.Run();
CanonicalCancellationSmoke.Run();
BundlerCancellationSmoke.Run();
PostBundleCancellationSmoke.Run();
CalliHeaderSmoke.Run();
MethodHeaderSmoke.Run();
FunctionPointerHeaderSmoke.Run();
LocalSignatureCountSmoke.Run();
NestedGenericCountSmoke.Run();
TypeSpecCountSmoke.Run();
ModuleAcquisitionSmoke.Run();
SerializerCancellationSmoke.Run();
AllocationOrchestrationSmoke.Run();
AllocatorCancellationSmoke.Run();
ManagedLinkCancellationSmoke.Run();
PclAdmissionSmoke.Run();
NativeAotCompileCancellationSmoke.Run();
ManagedClosureIdentitySmoke.Run();
SourceMapSmoke.Run();
MetadataReferenceArraySmoke.Run();
MetadataStringLiteralSmoke.Run();
AllocationTypeClosureSmoke.Run();
ReceiverAbiSmoke.Run(args.SingleOrDefault());
NativeConstantSmoke.Run();
NullableValueSmoke.Run();
ShiftSmoke.Run();
SwitchSmoke.Run();
ArgumentStoreSmoke.Run();
NestedGcLayoutSmoke.Run(args.SingleOrDefault());

public struct Payload { public int X; }
public static class Fixture
{
    public static Payload[] PassStructArray(Payload[] value) => value;
    public static byte[] Empty() => Array.Empty<byte>();
    public static byte[] Blob() => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    public static int OtherGeneric() => Identity(7);
    public static T Identity<T>(T value) => value;
}
