using HybridCPU.Compiler.Cil;

internal static class MetadataReferenceArraySmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(MetadataReferenceArrayFixture).Assembly.Location);
        RestrictedCilImportResultV1 direct = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(pe, new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.Create)));
        Check(direct.Diagnostics.Any(static diagnostic => diagnostic.Code == "HCCIL1401"),
            "A standalone import without a metadata-owned array binding must remain fail closed");

        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "metadata-reference-array-smoke",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.Create))], []));
        Check(graph.Status == RestrictedCilImportStatusV1.Success && graph.Graph is not null,
            "An exact metadata TypeDef reference array must acquire its SZARRAY descriptor and handle: " +
            string.Join(';', graph.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        ManagedCallGraphCompilationV1 typeTestGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "metadata-typedef-type-test",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.AsElement))], []));
        Check(typeTestGraph.Status == RestrictedCilImportStatusV1.Success && typeTestGraph.Graph is not null,
            "An exact local TypeDef cast target must acquire its runtime descriptor and handle: " +
            string.Join(';', typeTestGraph.Diagnostics));

        ManagedCallGraphCompilationV1 primitiveGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "metadata-primitive-array-smoke",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateInt))], [],
                MetadataOnlyModules:
                [new(File.ReadAllBytes(typeof(object).Assembly.Location), "primitive-corelib"),
                 new(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                     "primitive-runtime-facade")]));
        Check(primitiveGraph.Status == RestrictedCilImportStatusV1.Success && primitiveGraph.Graph is not null,
            "An exact CoreLib primitive TypeRef must acquire its primitive SZARRAY descriptor: " +
            string.Join(';', primitiveGraph.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        ManagedCallGraphCompilationV1 stringGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "metadata-string-array-smoke",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateString))], [],
                MetadataOnlyModules:
                [new(File.ReadAllBytes(typeof(object).Assembly.Location), "string-corelib"),
                 new(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                     "string-runtime-facade")]));
        Check(stringGraph.Status == RestrictedCilImportStatusV1.Success && stringGraph.Graph is not null,
            "An exact CoreLib String TypeRef must acquire an immutable reference SZARRAY descriptor: " +
            string.Join(';', stringGraph.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        ManagedCallGraphCompilationV1 jaggedGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "metadata-jagged-array-smoke",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateJagged))], [],
                MetadataOnlyModules:
                [new(File.ReadAllBytes(typeof(object).Assembly.Location), "jagged-corelib"),
                 new(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                     "jagged-runtime-facade")]));
        Check(jaggedGraph.Status == RestrictedCilImportStatusV1.Success && jaggedGraph.Graph is not null,
            "An exact primitive SZARRAY TypeSpec must bind the reference-element outer array: " +
            string.Join(';', jaggedGraph.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));

        ManagedCallGraphCompilationV1 enumGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "metadata-enum-array",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateEnum))], []));
        Check(enumGraph.Status == RestrictedCilImportStatusV1.Success,
            "Exact enum array allocation must use its nominal scalar-storage binding: " + string.Join(';', enumGraph.Diagnostics));
        ManagedCallGraphCompilationV1 referenceJagged = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "reference-jagged",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateReferenceJagged))], []));
        Check(referenceJagged.Status == RestrictedCilImportStatusV1.Success,
            "Exact reference SZARRAY TypeSpec must bind its outer array: " + string.Join(';', referenceJagged.Diagnostics));
        ManagedCallGraphCompilationV1 valueJagged = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "value-jagged-negative",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateValueJagged))], []));
        Check(valueJagged.Diagnostics.Any(static d => d.Code == "HCCIL1401"),
            "Unbound value-element SZARRAY TypeSpecs must remain fail closed");
        ManagedCallGraphCompilationV1 valueGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "metadata-value-array-negative",
                [new(typeof(MetadataReferenceArrayFixture).FullName!, nameof(MetadataReferenceArrayFixture.CreateValue))], []));
        Check(valueGraph.Diagnostics.Any(static diagnostic => diagnostic.Code == "HCCIL1401"),
            "Value-element arrays must remain behind their exact layout/GC binding path");
        Console.WriteLine("PASS metadata-owned reference SZARRAY binding and standalone/value fail-closed paths");
    }
}

public sealed class MetadataReferenceArrayElement;
public struct MetadataReferenceArrayValue { public int Value; }
public enum MetadataArrayEnum { First = -1, Last = int.MaxValue }
public static class MetadataReferenceArrayFixture
{
    public static MetadataReferenceArrayElement[] Create(int length) => new MetadataReferenceArrayElement[length];
    public static MetadataReferenceArrayElement? AsElement(object value) => value as MetadataReferenceArrayElement;
    public static int[] CreateInt(int length) => new int[length];
    public static string[] CreateString(int length) => new string[length];
    public static MetadataArrayEnum[] CreateEnum(int length) => new MetadataArrayEnum[length];
    public static int[][] CreateJagged(int length) => new int[length][];
    public static MetadataReferenceArrayElement[][] CreateReferenceJagged(int length) => new MetadataReferenceArrayElement[length][];
    public static MetadataReferenceArrayValue[][] CreateValueJagged(int length) => new MetadataReferenceArrayValue[length][];
    public static MetadataReferenceArrayValue[] CreateValue(int length) => new MetadataReferenceArrayValue[length];
}
