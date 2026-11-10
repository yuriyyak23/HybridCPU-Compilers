using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class NestedGcLayoutSmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(string? doomCorePath)
    {
        byte[] pe = File.ReadAllBytes(typeof(NestedGcHolder).Assembly.Location);
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        byte[][] references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "mscorlib.dll" }
            .Select(name => File.ReadAllBytes(Path.Combine(runtime, name))).ToArray();
        Products metadata = Inspect(pe, references);
        HybridCpuManagedTypeDescriptorV1 holder = metadata.Descriptors[nameof(NestedGcHolder)];
        Check(metadata.Descriptors[nameof(InterfaceStorageHolder)].InstanceFields.Single().StorageKind ==
            HybridCpuManagedStorageKindV1.ObjectReference, "Interface with nil BaseType retains reference storage, without granting DIM execution");
        Check(holder.InstanceSizeBytes == 64 && holder.StaticLayout.SizeBytes == 40,
            "Nullable payload size/alignment must include hasValue, padding, payload and trailing fields");
        Check(holder.InstanceFields.Where(f => f.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference).Select(f => f.OffsetBytes)
                .SequenceEqual(new[] { 40, 48, 56 }) && holder.StaticLayout.ObjectReferenceOffsets.SequenceEqual(new[] { 16, 24 }),
            "Nested instance/static GC offsets must be exact");
        Check(metadata.Fields[nameof(NestedGcHolder) + "::Value"].FieldType == RestrictedCilTypeV1.UnsupportedManaged &&
            metadata.Fields[nameof(NestedGcHolder) + "::Static"].FieldType == RestrictedCilTypeV1.UnsupportedManaged &&
            metadata.Fields[nameof(NestedGcHolder) + "::Array"].FieldType == RestrictedCilTypeV1.ObjectReference &&
            metadata.Initializers.ContainsKey(nameof(NestedGcHolder)), "Logical aggregate fields remain distinct from physical GC slots and retain cctor");
        HybridCpuManagedTypeDescriptorV1 deep = metadata.Descriptors[nameof(DeepGcHolder)];
        Check(deep.InstanceSizeBytes == 72 && deep.InstanceFields.Where(f => f.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
            .Select(f => f.OffsetBytes).SequenceEqual(new[] { 48, 56, 64 }), "Multiple nested nullable/struct levels must rebase every GC slot");
        Check(metadata.Descriptors[nameof(RecursiveArrayHolder)].InstanceFields.Single(f => f.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference).OffsetBytes == 16,
            "Recursion through SZARRAY is a reference, not infinite value layout");
        Check(!Inspect(pe, []).Descriptors.ContainsKey(nameof(NestedGcHolder)), "Missing Nullable definition must fail closed");
        Check(!metadata.Descriptors.ContainsKey(nameof(UnsupportedConstraintHolder)) && !metadata.Descriptors.ContainsKey(nameof(PackedNestedHolder)),
            "Unqualified generic constraints and packed payloads must fail closed");
        HybridCpuManagedTypeMetadataEncoderV1 encoder = new HybridCpuManagedTypeMetadataEncoderV1();
        HybridCpuManagedTypeMetadataArtifactV1 encoded = encoder.Encode([holder]);
        Check(encoded.Status == HybridCpuManagedTypeMetadataStatusV1.Encoded &&
            encoded.Digest == encoder.Encode([Inspect(pe, references.Reverse().ToArray()).Descriptors[nameof(NestedGcHolder)]]).Digest,
            "Physical metadata encoding must be deterministic across reference order");
        HybridCpuManagedTypeDescriptorV1 missingMap = holder with { StaticLayout = holder.StaticLayout with { ObjectReferenceOffsets = [16] } };
        missingMap = missingMap with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(missingMap) };
        Check(encoder.Encode([missingMap]).Status == HybridCpuManagedTypeMetadataStatusV1.InvalidInput, "Omitted static GC slot must fail metadata validation");
        HybridCpuManagedTypeDescriptorV1 wrongSlot = holder with { InstanceFields = holder.InstanceFields.Select(f => f.OffsetBytes == 40 ? f with { SizeBytes = 4, AlignmentBytes = 4 } : f).ToArray() };
        wrongSlot = wrongSlot with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(wrongSlot) };
        Check(encoder.Encode([wrongSlot]).Status == HybridCpuManagedTypeMetadataStatusV1.InvalidInput, "Reference slots must remain eight-byte aligned/width");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            fieldLayouts: metadata.Fields.Values.ToArray(), typeInitializationBindings: metadata.Initializers.Values.ToArray());
        Check(importer.ImportImage(pe, new(nameof(NestedGcHolder), nameof(NestedGcHolder.Copy))).Diagnostics.Any(d => d.Code == "HCCIL1209"),
            "Logical GC aggregate copy requires its own write-barrier/lifetime lowering");
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "nested-gc-smoke",
            [new(nameof(NestedGcHolder), nameof(NestedGcHolder.Ping))], []));
        Check(graph.Status == RestrictedCilImportStatusV1.Success && graph.Graph!.Edges.Any(e => e.IsTypeInitializerTarget),
            "Existing explicit metadata binding retains cctor on a scalar method: " + string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ExerciseGc(holder);
        // CLR semantic reference: nullable presence and value copies preserve object identities,
        // while mutating a copied struct does not modify its original. No CLR physical-layout claim.
        object first = new object(); object second = new object();
        NestedGcPayload? original = new NestedGcPayload { Number = 9, First = first, Second = second };
        NestedGcPayload copy = original.Value; copy.First = null;
        Check(original.HasValue && ReferenceEquals(original.Value.First, first) && ReferenceEquals(original.Value.Second, second) &&
            copy.First is null && !default(NestedGcPayload?).HasValue, "CoreCLR nullable/value-copy reference behavior");
        if (doomCorePath is not null)
        {
            Products doom = Inspect(File.ReadAllBytes(doomCorePath), references);
            Check(doom.Descriptors.ContainsKey("DoomSharp.Core.DoomGame"), "DoomGame descriptor must remain available");
            const string termination = "DoomSharp.Core.DoomTerminationException";
            Check(doom.Descriptors.TryGetValue(termination, out HybridCpuManagedTypeDescriptorV1? terminationDescriptor) &&
                terminationDescriptor.BaseTypeId is not null &&
                doom.Fields.TryGetValue(termination + "::<ExitCode>k__BackingField", out RestrictedCilFieldLayoutBindingV1? exitCode) &&
                exitCode.FieldType == RestrictedCilTypeV1.Int32 &&
                terminationDescriptor.InstanceFields.Single(f => f.Identity == "<ExitCode>k__BackingField").OffsetBytes >=
                HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes,
                "Actual Doom termination exception must resolve its forwarded CoreLib base and exact ExitCode layout; " +
                $"termination={doom.Descriptors.ContainsKey(termination)}, exception={doom.Descriptors.ContainsKey("System.Exception")}, " +
                $"field={doom.Fields.ContainsKey(termination + "::<ExitCode>k__BackingField")}");
            const string owner = "DoomSharp.Core.UI.IntermissionController";
            Check(doom.Descriptors.ContainsKey(owner) && doom.Initializers.ContainsKey(owner) &&
                doom.Fields[owner + "::_background"].FieldType == RestrictedCilTypeV1.ObjectReference,
                "Actual Doom Patch field must use the ordinary reference carrier");
            Check(doom.Descriptors[owner].InstanceFields.Single(f => f.Identity == "_background").StorageKind ==
                HybridCpuManagedStorageKindV1.ObjectReference,
                "Actual Doom Patch field must expose one exact physical GC slot");
            Console.WriteLine("PASS actual Doom immutable Patch reference layout/cctor and exact GC slot");
        }
        Console.WriteLine("PASS nested/closed-generic GC layouts, logical copy gate, metadata negatives and real runtime GC retention/reclamation");
    }

    private static void ExerciseGc(HybridCpuManagedTypeDescriptorV1 descriptor)
    {
        HybridCpuManagedFieldDeclarationV1[] fields = descriptor.InstanceFields.Select(f => (Field: f, Static: false))
            .Concat(descriptor.StaticLayout.Fields.Select(f => (Field: f, Static: true)))
            .Select((row, index) => new HybridCpuManagedFieldDeclarationV1(row.Field.Identity, row.Field.StorageKind,
                row.Field.SizeBytes, row.Field.AlignmentBytes, row.Static, index)).ToArray();
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new(descriptor.StableIdentity, HybridCpuManagedTypeKindV1.Class, null, [], fields),
            new("GcSentinel", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        HybridCpuManagedTypeDescriptorV1 runtimeDescriptor = types.Descriptors.Single(d => d.StableIdentity == descriptor.StableIdentity);
        Check(runtimeDescriptor.DescriptorDigest == descriptor.DescriptorDigest, "Existing runtime must reproduce the compiler physical layout exactly");
        ulong Handle(string name) => types.TypeHandle(types.Descriptors.Single(d => d.StableIdentity == name).TypeId)!.Value;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap init");
        ulong root = heap.Allocate(Handle(descriptor.StableIdentity)).ObjectReference;
        ulong first = heap.Allocate(Handle("GcSentinel")).ObjectReference;
        ulong second = heap.Allocate(Handle("GcSentinel")).ObjectReference;
        ulong statik = heap.Allocate(Handle("GcSentinel")).ObjectReference;
        ulong garbage = heap.Allocate(Handle("GcSentinel")).ObjectReference;
        void Write(int offset, ulong value)
        {
            byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            Check(heap.WriteObjectBytes(root, offset, bytes).IsSuccess, "GC fixture write");
        }
        Write(40, first); Write(48, second); Write(32, garbage); // hasValue at 24 remains false: GC slots are unconditional.
        HybridCpuManagedStaticFieldRuntimeV1 staticFields = new HybridCpuManagedStaticFieldRuntimeV1(types);
        Check(staticFields.StoreReference(Handle(descriptor.StableIdentity), 16, statik).IsSuccess,
            "Exact static reference helper store");
        Check(unchecked((ulong)staticFields.LoadReference(Handle(descriptor.StableIdentity), 16).ScalarValue) == statik,
            "Exact static reference helper load");
        byte[] staticSnapshot = types.StaticStorage(runtimeDescriptor.TypeId)!.ToArray();
        Check(staticFields.StoreReference(Handle(descriptor.StableIdentity), 8, statik).Status ==
                  HybridCpuManagedShapeStatusV1.InvalidType &&
              staticSnapshot.SequenceEqual(types.StaticStorage(runtimeDescriptor.TypeId)!),
            "Wrong static field kind/offset must fail atomically");
        Check(staticFields.LoadReference(Handle(descriptor.StableIdentity), 8).Status ==
                  HybridCpuManagedShapeStatusV1.InvalidType,
            "Static reference load must reject a non-reference field");
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest, abi.TargetContractDigest,
            abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedGcRootV1[] roots = new HybridCpuManagedGcRootV1[] { new("holder", HybridCpuManagedGcRootSourceV1.Handle, root) };
        HybridCpuManagedNonMovingGcResultV1 collected = gc.Collect(new([], [], roots), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Check(collected.IsSuccess && collected.ReachableObjects.Contains(first) && collected.ReachableObjects.Contains(second) &&
            collected.ReachableObjects.Contains(statik) && collected.ReclaimedObjects.Contains(garbage),
            "GC must traverse nested physical slots and statics, ignoring pointer-like primitive bytes: " + collected.Reason);
        Write(40, 0); Write(48, 0);
        Check(staticFields.StoreReference(Handle(descriptor.StableIdentity), 16, 0).IsSuccess,
            "Exact static reference helper clear");
        HybridCpuManagedNonMovingGcResultV1 released = gc.Collect(new([], [], roots), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Check(released.IsSuccess && new[] { first, second, statik }.All(released.ReclaimedObjects.Contains),
            "Clearing nested slots must allow reclamation");
    }

    private sealed record Products(IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1> Descriptors,
        IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1> Fields,
        IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1> Initializers);

    private static Products Inspect(byte[] pe, IReadOnlyList<byte[]> references)
    {
        Type importer = typeof(RestrictedCilImporterV1);
        Type context = importer.GetNestedType("ManagedModuleContext", BindingFlags.NonPublic)!;
        List<IDisposable> disposal = new List<IDisposable>();
        Array Wrap(IEnumerable<byte[]> images)
        {
            byte[][] list = images.ToArray(); Array array = Array.CreateInstance(context, list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                IDisposable item = (IDisposable)Activator.CreateInstance(context, new object[] { (ReadOnlyMemory<byte>)list[i], "nested-layout" })!;
                disposal.Add(item); array.SetValue(item, i);
            }
            return array;
        }
        try
        {
            object result = importer.GetMethod("BuildMetadataBindings", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { Wrap([pe]), Wrap(references) })!;
            return new((IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1>)result.GetType().GetProperty("Descriptors")!.GetValue(result)!,
                (IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1>)result.GetType().GetProperty("Fields")!.GetValue(result)!,
                (IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1>)result.GetType().GetProperty("TypeInitializers")!.GetValue(result)!);
        }
        finally { foreach (IDisposable item in disposal) item.Dispose(); }
    }
}

public struct NestedGcPayload { public int Number; public object? First; public object? Second; }
public class NestedGcHolder
{
    public byte Header;
    public NestedGcPayload? Value;
    public NestedGcPayload?[]? Array;
    public static NestedGcPayload? Static;
    public static int Marker;
    static NestedGcHolder() { Marker = 7; }
    public static int Ping() => Marker;
    public static void Copy(NestedGcHolder to, NestedGcHolder from) { to.Value = from.Value; }
}
public struct DeepGcPayload { public byte First; public NestedGcPayload? Inner; public object? Last; }
public class DeepGcHolder { public DeepGcPayload? Value; }
public struct RecursiveArrayPayload { public RecursiveArrayPayload[]? Children; public int Value; }
public class RecursiveArrayHolder { public RecursiveArrayPayload Value; }
public struct ConstrainedGcPayload<T> where T : IDisposable { public T Value; }
public class UnsupportedConstraintHolder { public ConstrainedGcPayload<MemoryStream> Value; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedGcPayload { public byte First; public object? Value; }
public class PackedNestedHolder { public PackedGcPayload? Value; }
public interface IStorageFixture { int Read() => 7; }
public class InterfaceStorageHolder { public IStorageFixture? Value; }
