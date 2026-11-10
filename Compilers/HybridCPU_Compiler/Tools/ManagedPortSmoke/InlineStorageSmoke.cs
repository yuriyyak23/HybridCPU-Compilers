using System.Reflection;
using System.Runtime.InteropServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Platform.Contracts;

internal static class InlineStorageSmoke
{
    public static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        byte[] pe = File.ReadAllBytes(typeof(InlineHolder).Assembly.Location);
        // Inspect compiler-private metadata products, without exporting test-only compiler APIs.
        Type importer = typeof(RestrictedCilImporterV1);
        Type moduleType = importer.GetNestedType("ManagedModuleContext", BindingFlags.NonPublic)!;
        using IDisposable module = (IDisposable)Activator.CreateInstance(moduleType, new object[] { (ReadOnlyMemory<byte>)pe, "inline-smoke" })!;
        Array modules = Array.CreateInstance(moduleType, 1);
        modules.SetValue(module, 0);
        object result = importer.GetMethod("BuildMetadataBindings", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { modules, null })!;
        IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1> descriptors = (IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1>)result.GetType()
            .GetProperty("Descriptors")!.GetValue(result)!;
        IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1> initializers = (IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1>)result.GetType()
            .GetProperty("TypeInitializers")!.GetValue(result)!;
        HybridCpuManagedTypeDescriptorV1 holder = descriptors[typeof(InlineHolder).FullName!];
        HybridCpuManagedFieldLayoutV1 field = holder.InstanceFields.Single(f => f.Identity == nameof(InlineHolder.Payload));
        Check(field.StorageKind == HybridCpuManagedStorageKindV1.BlittableValue &&
            field.SizeBytes == Marshal.SizeOf<InlineNested>() && field.AlignmentBytes == 4 && field.OffsetBytes == 20,
            "Nested inline payload size, alignment and object offset");
        Check(holder.InstanceFields.Single(f => f.Identity == nameof(InlineHolder.Reference)).OffsetBytes == 32,
            "Reference following inline payload must remain aligned and GC-visible");
        Check(holder.InstanceFields.Single(f => f.Identity == nameof(InlineHolder.Reference)).StorageKind ==
            HybridCpuManagedStorageKindV1.ObjectReference, "Reference cannot become inline bytes");
        HybridCpuManagedFieldLayoutV1 self = holder.StaticLayout.Fields.Single(f => f.Identity == nameof(InlineHolder.Fixed));
        Check(self.SizeBytes == 4 && self.StorageKind == HybridCpuManagedStorageKindV1.BlittableValue,
            "Self-typed static fields must not cause payload recursion");
        Check(initializers.ContainsKey(typeof(InlineHolder).FullName!), "Holder cctor must retain exact binding");
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "inline-smoke",
                [new(typeof(InlineHolder).FullName!, nameof(InlineHolder.Ping))], []));
        Check(graph.Status == RestrictedCilImportStatusV1.Success && graph.Graph!.Edges.Any(e => e.IsTypeInitializerTarget),
            "Inline holder must admit scalar method while preserving cctor reachability: " +
            string.Join(";", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        HybridCpuManagedFieldLayoutV1 nestedReference = descriptors[typeof(RefInlineHolder).FullName!].InstanceFields.Single();
        Check(nestedReference.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference && nestedReference.OffsetBytes == 16 &&
            nestedReference.SizeBytes == 8, "Nested GC reference must become an exact physical root slot, not blittable bytes");
        Check(!descriptors.ContainsKey(typeof(PackedInlineHolder).FullName!), "Packed inline payload must fail closed");
        Check(!descriptors.ContainsKey(typeof(ExplicitInlineHolder).FullName!), "Explicit inline payload must fail closed");
        IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1> fields = (IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1>)result.GetType()
            .GetProperty("Fields")!.GetValue(result)!;
        Check(fields[typeof(InlineHolder).FullName! + "::Payload"].FieldType == RestrictedCilTypeV1.UnsupportedManaged,
            "An aggregate is not a scalar carrier even when its size is four bytes");
        Check(fields[typeof(RefInlineHolder).FullName! + "::Payload"].FieldType == RestrictedCilTypeV1.UnsupportedManaged,
            "Flattened GC slots must not turn the logical aggregate field into a scalar");
        RestrictedCilImportResultV1 scalarized = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            fieldLayouts: fields.Values.ToArray(), typeInitializationBindings: initializers.Values.ToArray())
            .ImportImage(pe, new(typeof(InlineHolder).FullName!, nameof(InlineHolder.Copy)));
        Check(scalarized.Diagnostics.Any(d => d.Code == "HCCIL1810"), "Exact reference-free aggregate flow must retain the pre-IR lowering gate");
        Console.WriteLine("PASS inline storage: nested size/alignment, self-static, GC/packed/explicit guards, no scalarization");
    }
}

public struct InlineFixed : IComparable<InlineFixed>
{
    public int Value;
    public static readonly InlineFixed Zero = new();
    public int CompareTo(InlineFixed other) => Value.CompareTo(other.Value);
}
public enum InlineEnum : short { Zero }
public struct InlineNested { public byte Head; public InlineFixed Fixed; public InlineEnum Tail; }
public class InlineHolder
{
    public byte Head;
    public InlineNested Payload;
    public object? Reference;
    public static InlineFixed Fixed;
    public static int Marker;
    static InlineHolder() { Marker = 1; }
    public static int Ping() => 42;
    public static void Copy(InlineHolder to, InlineHolder from) { to.Payload = from.Payload; }
}
public struct RefInline { public object? Reference; }
public class RefInlineHolder { public RefInline Payload; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedInline { public byte Head; public int Tail; }
public class PackedInlineHolder { public PackedInline Payload; }
[StructLayout(LayoutKind.Explicit)]
public struct ExplicitInline { [FieldOffset(0)] public int Value; }
public class ExplicitInlineHolder { public ExplicitInline Payload; }
