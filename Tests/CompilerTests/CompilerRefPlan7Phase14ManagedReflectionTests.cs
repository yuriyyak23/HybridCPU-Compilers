using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU_ISE.Tests.TestHelpers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase14ManagedReflectionTests
{
    [Fact]
    public void ReflectionContract_IsOptInBoundedAndAddsNoOpcodeOrDynamicCode()
    {
        Assert.Equal("hybridcpu.managed-reflection/v1", HybridCpuManagedReflectionContractV1.SchemaId);
        Assert.False(HybridCpuManagedReflectionRetentionOptionsV1.Production.Enabled);
        Assert.False(HybridCpuManagedReflectionOptionsV1.Production.Enabled);
        Assert.Contains(RestrictedCilSupportMatrixV1.Default.Features, static feature =>
            feature.Feature == "reflection-bounded" && feature.Support == RestrictedCilMatrixSupportV1.Supported);
        Assert.Contains(RestrictedCilSupportMatrixV1.Default.Features, static feature =>
            feature.Feature == "reflection-dynamic" && feature.Support == RestrictedCilMatrixSupportV1.Unsupported);
        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff,
            HybridCpuManagedFeatureSetV1.Default.Workstreams.Single(static row =>
                row.Identity == "bounded-public-reflection").Support);
        Assert.DoesNotContain(Enum.GetNames<HybridCpuOpcode>(), static name =>
            name.Contains("REFLECT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitRoots_RetainSelectedPublicMembersAndTrimEverythingElse()
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRetentionResultV1 result = Retain(fixture,
            [new("Phase14.Widget", ["Phase14.Widget.PublicValue"])]);

        Assert.True(result.IsSuccess, result.Reason);
        HybridCpuManagedReflectionTypeV1 type = Assert.Single(result.Table!.Types);
        Assert.Equal("Phase14.Widget", type.StableIdentity);
        HybridCpuManagedReflectionMemberV1 member = Assert.Single(type.Members);
        Assert.Equal("PublicValue", member.Name);
        Assert.DoesNotContain(type.Members, static row => row.StableIdentity.Contains("Hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Table.Types, static row => row.StableIdentity.Contains("Box", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Phase14.Missing", "Phase14.Missing.Member")]
    [InlineData("Phase14.Widget", "Phase14.Widget.Hidden")]
    public void MissingOrNonPublicMetadata_FailsWithExplicitDiagnostic(string type, string member)
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRetentionResultV1 result = Retain(fixture, [new(type, [member])]);

        Assert.Equal(HybridCpuManagedReflectionRetentionStatusV1.MissingMetadata, result.Status);
        Assert.Contains("absent, non-public", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Table);
    }

    [Fact]
    public void RetainedTableOrderingAndDigest_AreIndependentOfInputAndRootOrder()
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRootV1[] roots =
        [
            new("Phase14.Box<System.Int64>", ["Phase14.Box<System.Int64>.Item"]),
            new("Phase14.Widget", ["Phase14.Widget.Run", "Phase14.Widget.PublicValue"])
        ];
        HybridCpuManagedReflectionRetentionResultV1 first = Retain(fixture, roots);
        HybridCpuManagedReflectionRetentionResultV1 second = new HybridCpuManagedReflectionMetadataBuilderV1().Build(
            fixture.Types.Descriptors.Reverse().ToArray(), fixture.Members.Reverse().ToArray(), roots.Reverse().ToArray(),
            HybridCpuManagedReflectionRetentionOptionsV1.Qualification);

        Assert.True(first.IsSuccess && second.IsSuccess, $"{first.Reason} {second.Reason}");
        Assert.Equal(first.Table!.TableDigest, second.Table!.TableDigest);
        Assert.Equal(first.MetadataBytes, second.MetadataBytes);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.True(HybridCpuManagedReflectionContractV1.IsValid(first.Table));
        Assert.True(first.Table.Types.SequenceEqual(first.Table.Types.OrderBy(static type => type.TypeId)));
    }

    [Fact]
    public void ExactConstructedGenericIdentities_ProduceDistinctPublicTypeObjects()
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRetentionResultV1 retained = Retain(fixture,
        [
            new("Phase14.Box<System.Int32>", []),
            new("Phase14.Box<System.Int64>", [])
        ]);
        HybridCpuManagedReflectionRuntimeV1 runtime = new HybridCpuManagedReflectionRuntimeV1(retained.Table!, HybridCpuManagedReflectionOptionsV1.Qualification);
        HybridCpuManagedReflectionResultV1 int32 = runtime.GetType("Phase14.Box<System.Int32>");
        HybridCpuManagedReflectionResultV1 int64 = runtime.GetType("Phase14.Box<System.Int64>");

        Assert.True(int32.IsSuccess && int64.IsSuccess);
        Assert.True(int32.Type!.IsExactConstructedGeneric);
        Assert.True(int64.Type!.IsExactConstructedGeneric);
        Assert.NotEqual(int32.Type.RuntimeTypeId, int64.Type.RuntimeTypeId);
        Assert.NotEqual(int32.Type.ReflectionObjectId, int64.Type.ReflectionObjectId);
    }

    [Fact]
    public void Runtime_CachesTypeIdentityAndPerformsOnlyRetainedMemberLookup()
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRetentionResultV1 retained = Retain(fixture,
            [new("Phase14.Widget", ["Phase14.Widget.PublicValue", "Phase14.Widget.Run"])]);
        HybridCpuManagedReflectionRuntimeV1 runtime = new HybridCpuManagedReflectionRuntimeV1(retained.Table!, HybridCpuManagedReflectionOptionsV1.Qualification);
        ulong typeId = retained.Table.Types.Single().TypeId;
        HybridCpuManagedReflectionResultV1 first = runtime.GetType(typeId);
        HybridCpuManagedReflectionResultV1 second = runtime.GetType(typeId);
        HybridCpuManagedReflectionResultV1 member = runtime.GetMembers(first.Type!, "Run");

        Assert.Same(first.Type, second.Type);
        Assert.Equal(1, runtime.CachedTypeCount);
        Assert.Equal("Run", Assert.Single(member.Members).Name);
        Assert.Equal(HybridCpuManagedReflectionStatusV1.MissingMetadata,
            runtime.GetMembers(first.Type!, "Hidden").Status);
    }

    [Fact]
    public void DisabledMissingAndDynamicRequests_FailClosedWithoutFallback()
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRetentionResultV1 retained = Retain(fixture, [new("Phase14.Widget", [])]);
        HybridCpuManagedReflectionRuntimeV1 disabled = new HybridCpuManagedReflectionRuntimeV1(retained.Table!);
        HybridCpuManagedReflectionRuntimeV1 runtime = new HybridCpuManagedReflectionRuntimeV1(retained.Table!, HybridCpuManagedReflectionOptionsV1.Qualification);

        Assert.Equal(HybridCpuManagedReflectionStatusV1.Disabled,
            disabled.GetType(retained.Table!.Types.Single().TypeId).Status);
        Assert.Equal(HybridCpuManagedReflectionStatusV1.MissingMetadata,
            runtime.GetType("Phase14.Trimmed").Status);
        Assert.Equal(HybridCpuManagedReflectionStatusV1.UnsupportedDynamicCode,
            runtime.RequestDynamicCode("Reflection.Emit").Status);
    }

    [Fact]
    public void BudgetsAndDigestTamper_FailClosed()
    {
        Fixture fixture = BuildFixture();
        HybridCpuManagedReflectionRetentionOptionsV1 oneType = HybridCpuManagedReflectionRetentionOptionsV1.Create(true, 1, 8);
        HybridCpuManagedReflectionRetentionResultV1 exhausted = new HybridCpuManagedReflectionMetadataBuilderV1().Build(
            fixture.Types.Descriptors, fixture.Members,
            [new("Phase14.Widget", []), new("Phase14.Box<System.Int32>", [])], oneType);
        HybridCpuManagedReflectionRetentionResultV1 retained = Retain(fixture, [new("Phase14.Widget", [])]);
        HybridCpuManagedReflectionTableV1 tampered = retained.Table! with { TableDigest = new string('0', 64) };
        HybridCpuManagedReflectionRuntimeV1 runtime = new HybridCpuManagedReflectionRuntimeV1(tampered, HybridCpuManagedReflectionOptionsV1.Qualification);

        Assert.Equal(HybridCpuManagedReflectionRetentionStatusV1.BudgetExhausted, exhausted.Status);
        Assert.Equal(HybridCpuManagedReflectionStatusV1.InvalidMetadata,
            runtime.GetType(retained.Table.Types.Single().TypeId).Status);
    }

    [Fact]
    public void KernelAndIseSources_DoNotParseOrOwnReflectionMetadata()
    {
        string root = CompatFreezeScanner.FindRepoRoot();
        string[] roots =
        [
            Path.Combine(CompatFreezeScanner.FindCompilerRepoRoot(), "Compilers", "HybridCPU_RuntimeKernel"),
            Path.Combine(root, "HybridCPU_ISE", "CloseToHSL", "Core")
        ];
        string[] forbidden = ["HybridCpuManagedReflection", "managed-reflection", ".hcreflect", "Reflection.Emit"];
        foreach (string sourceRoot in roots)
        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            Assert.All(forbidden, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
        }
    }

    private static HybridCpuManagedReflectionRetentionResultV1 Retain(Fixture fixture,
        IReadOnlyList<HybridCpuManagedReflectionRootV1> roots) => new HybridCpuManagedReflectionMetadataBuilderV1().Build(
        fixture.Types.Descriptors, fixture.Members, roots, HybridCpuManagedReflectionRetentionOptionsV1.Qualification);

    private static Fixture BuildFixture()
    {
        HybridCpuManagedTypeDeclarationV1[] declarations =
        [
            new("Phase14.Widget", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("Phase14.Box<System.Int32>", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("Phase14.Box<System.Int64>", HybridCpuManagedTypeKindV1.Class, null, [], [])
        ];
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build(declarations);
        Assert.True(built.IsSuccess, built.Reason);
        HybridCpuManagedReflectionMemberDeclarationV1[] members =
        [
            new("Phase14.Widget", "Phase14.Widget.PublicValue", "PublicValue",
                HybridCpuManagedReflectionMemberKindV1.Field, "System.Int32", true, false, 0),
            new("Phase14.Widget", "Phase14.Widget.Hidden", "Hidden",
                HybridCpuManagedReflectionMemberKindV1.Method, "():System.Void", false, false, 1),
            new("Phase14.Widget", "Phase14.Widget.Run", "Run",
                HybridCpuManagedReflectionMemberKindV1.Method, "():System.Int32", true, false, 2),
            new("Phase14.Box<System.Int32>", "Phase14.Box<System.Int32>.Item", "Item",
                HybridCpuManagedReflectionMemberKindV1.Property, "System.Int32", true, false, 0),
            new("Phase14.Box<System.Int64>", "Phase14.Box<System.Int64>.Item", "Item",
                HybridCpuManagedReflectionMemberKindV1.Property, "System.Int64", true, false, 0)
        ];
        return new(built.TypeSystem!, members);
    }

    private sealed record Fixture(HybridCpuManagedTypeSystemV1 Types,
        IReadOnlyList<HybridCpuManagedReflectionMemberDeclarationV1> Members);
}
