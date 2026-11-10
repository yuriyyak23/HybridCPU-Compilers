using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan6Phase02ManagedCallGraphTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(ManagedCallGraphFixtures).Assembly.Location);
    private static readonly string FixtureType = typeof(ManagedCallGraphFixtures).FullName!;

    [Fact]
    public void AcyclicChain_ImportsEveryMethodThroughCanonicalBoundaryInCalleeFirstOrder()
    {
        ManagedCallGraphCompilationV1 result = Import(nameof(ManagedCallGraphFixtures.Root));

        AssertSuccess(result);
        Assert.Equal(3, result.Methods.Count);
        Assert.Equal(2, result.Graph!.Edges.Count);
        Assert.Equal(3, result.Graph.MaximumAcyclicDepth);
        Assert.All(result.Methods, method =>
        {
            Assert.Equal(IrFrontendAdapterStatus.Success,
                CanonicalIrFrontendBoundaryV1.Validate(Assert.IsType<IrProgram>(method.Import.Program)).Status);
            Assert.Equal(method.Identity.StableIdentity, method.Import.Provenance!.MethodIdentity);
        });
        foreach (ManagedCallGraphEdgeV1 edge in result.Graph.Edges)
            Assert.True(IndexOf(result.Graph.CompilationOrder, edge.CalleeIdentity) <
                IndexOf(result.Graph.CompilationOrder, edge.CallerIdentity));
    }

    [Fact]
    public void SharedCallee_IsDeduplicatedAndEdgesRemainCallSiteExact()
    {
        ManagedCallGraphCompilationV1 result = Import(nameof(ManagedCallGraphFixtures.DiamondRoot));

        AssertSuccess(result);
        Assert.Equal(4, result.Methods.Count);
        Assert.Equal(4, result.Graph!.Edges.Count);
        Assert.Equal(4, result.Graph.Edges.Select(static edge => edge.StableId).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(result.Methods, static method => method.Identity.MethodName == nameof(ManagedCallGraphFixtures.Shared));
        Assert.Equal(result.Graph.Edges.OrderBy(static edge => edge.CallerIdentity, StringComparer.Ordinal)
                .ThenBy(static edge => edge.CallerIlOffset).ThenBy(static edge => edge.CalleeIdentity, StringComparer.Ordinal),
            result.Graph.Edges);
    }

    [Fact]
    public void RepeatedCallsAndLoopingCallee_AreDiscoveredByExactCallSite()
    {
        ManagedCallGraphCompilationV1 repeated = Import(nameof(ManagedCallGraphFixtures.RepeatedRoot));
        AssertSuccess(repeated);
        Assert.Equal(2, repeated.Methods.Count);
        Assert.Equal(2, repeated.Graph!.Edges.Count);
        Assert.Single(repeated.Graph.Edges.Select(static edge => edge.CalleeIdentity).Distinct(StringComparer.Ordinal));

        ManagedCallGraphCompilationV1 looping = Import(nameof(ManagedCallGraphFixtures.LoopRoot));
        AssertSuccess(looping);
        ManagedCompiledMethodV1 helper = looping.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.LoopHelper));
        Assert.NotEmpty(helper.Import.ControlFlowAnalysis!.Loops);
    }

    [Fact]
    public void PrimitiveAndVoidCallSignatures_AreRepresentedWithoutInventingValues()
    {
        ManagedCallGraphCompilationV1 primitive = Import(nameof(ManagedCallGraphFixtures.PrimitiveRoot));
        AssertSuccess(primitive);
        Assert.Contains(primitive.Methods, static method =>
            method.Identity.CanonicalSignature == "(System.Int32,System.Int32):System.Int32");

        ManagedCallGraphCompilationV1 voidCall = Import(nameof(ManagedCallGraphFixtures.VoidRoot));
        AssertSuccess(voidCall);
        IrInstruction call = Assert.Single(voidCall.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.VoidRoot)).Import.Program!.Instructions,
                static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        Assert.DoesNotContain(call.Annotation.Defs,
            static operand => operand.Kind == IrOperandKind.VirtualValue);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters,
            call.Annotation.Defs.Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
                .Select(static operand => checked((int)operand.Value)));

        ManagedCallGraphCompilationV1 noArgumentVoid = Import(nameof(ManagedCallGraphFixtures.NoArgumentVoidRoot));
        AssertSuccess(noArgumentVoid);
    }

    [Fact]
    public void StableIdentityAndGraph_DoNotDependOnPathSourceIdentityOrMvid()
    {
        ManagedCallGraphCompilationV1 first = Import(nameof(ManagedCallGraphFixtures.Root), "path-a.dll");
        ManagedCallGraphCompilationV1 second = Import(nameof(ManagedCallGraphFixtures.Root), "different/path-b.dll");

        AssertSuccess(first);
        AssertSuccess(second);
        Assert.Equal(first.Graph!.GraphDigest, second.Graph!.GraphDigest);
        Assert.Equal(first.Graph.MethodIdentities, second.Graph.MethodIdentities);
        Assert.All(first.Methods, static method =>
        {
            RestrictedCilProvenanceV1 provenance = method.Import.Provenance!;
            Assert.True(provenance.MethodIdentityHasLinkageAuthority);
            Assert.False(provenance.MethodLocalIdentityHasLinkageAuthority);
            Assert.StartsWith("mmod:", provenance.ManagedModuleIdentity, StringComparison.Ordinal);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(provenance.ManagedModuleIdentity), 1, 128);
        });
        Assert.All(first.Methods, static method =>
        {
            Assert.False(method.Identity.DependsOnFileSystemPath);
            Assert.False(method.Identity.DependsOnMvid);
            Assert.Equal(64, method.Identity.IdentityDigest.Length);
            Assert.StartsWith("mmid:", method.Identity.StableIdentity, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PresentedDiscoveryOrderAndSerialization_AreDeterministic()
    {
        RestrictedCilMethodSelectorV1 root = Selector(nameof(ManagedCallGraphFixtures.DiamondRoot));
        RestrictedCilMethodSelectorV1[] bodies =
        [
            root,
            Selector(nameof(ManagedCallGraphFixtures.Left)),
            Selector(nameof(ManagedCallGraphFixtures.Right)),
            Selector(nameof(ManagedCallGraphFixtures.Shared))
        ];
        ManagedCallGraphCompilationV1 first = ImportWorld(ManagedBodyWorldModeV1.AdapterPresented, [root], bodies);
        ManagedCallGraphCompilationV1 second = ImportWorld(ManagedBodyWorldModeV1.AdapterPresented, [root], bodies.Reverse().ToArray());

        AssertSuccess(first);
        AssertSuccess(second);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.Graph),
            System.Text.Json.JsonSerializer.Serialize(second.Graph));
        Assert.Equal(first.Graph!.MethodIdentities.Count,
            first.Graph.MethodIdentities.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AdapterPresentedWorld_OwnsAvailableBodiesButNotCompilerAdmissionOrReachability()
    {
        RestrictedCilMethodSelectorV1 root = Selector(nameof(ManagedCallGraphFixtures.Root));
        ManagedCallGraphCompilationV1 missing = ImportWorld(ManagedBodyWorldModeV1.AdapterPresented,
            [root], [root]);
        AssertFailure(missing, RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1003");

        ManagedCallGraphCompilationV1 complete = ImportWorld(ManagedBodyWorldModeV1.AdapterPresented,
            [root], [root, Selector(nameof(ManagedCallGraphFixtures.Middle)), Selector(nameof(ManagedCallGraphFixtures.Leaf))]);
        AssertSuccess(complete);
        Assert.Equal(ManagedBodyWorldModeV1.AdapterPresented, complete.Graph!.BodyWorldMode);
        Assert.False(complete.Graph.OwnsNativeAotReachability);
        Assert.False(complete.Graph.HasRuntimeAuthority);
    }

    [Theory]
    [InlineData(nameof(ManagedCallGraphFixtures.SelfRecursive))]
    [InlineData(nameof(ManagedCallGraphFixtures.MutualA))]
    public void RecursiveScc_IsRejectedWithStableDeferredDiagnostic(string root)
    {
        ManagedCallGraphCompilationV1 first = Import(root);
        ManagedCallGraphCompilationV1 second = Import(root);

        AssertFailure(first, RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1001");
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Empty(first.Methods);
        Assert.Null(first.Graph);
    }

    [Fact]
    public void ExternalBodyAndConstrainedGenericMethod_FailClosed()
    {
        AssertFailure(Import(nameof(ManagedCallGraphFixtures.ExternalCall)), RestrictedCilImportStatusV1.Unsupported,
            "HCSCF-BODYWORLD1002");
        AssertFailure(Import(nameof(ManagedCallGraphFixtures.GenericCall)), RestrictedCilImportStatusV1.Unsupported,
            "HCCIL1702");
    }

    [Theory]
    [InlineData(nameof(ManagedCallGraphFixtures.VirtualCall))]
    [InlineData(nameof(ManagedCallGraphFixtures.VirtualCallOnExisting))]
    public void QualifiedDynamicDispatchAndMetadataOwnedAllocation_Succeed(string root)
    {
        ManagedCallGraphCompilationV1 result = Import(root);
        AssertSuccess(result);
    }

    [Fact]
    public void AggregateSignature_ReachesTheExactPreIrGate()
    {
        ManagedCallGraphCompilationV1 result = Import(nameof(ManagedCallGraphFixtures.UnsupportedSignatureRoot));
        AssertFailure(result, RestrictedCilImportStatusV1.Unsupported, "HCCIL1810");
    }

    [Fact]
    public void ExactMetadataToken_SelectsOverloadAndParticipatesInIdentity()
    {
        int token = typeof(ManagedCallGraphFixtures).GetMethod(nameof(ManagedCallGraphFixtures.Overload), [typeof(int)])!.MetadataToken;
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(FixtureType, nameof(ManagedCallGraphFixtures.Overload), token);
        ManagedCallGraphCompilationV1 result = ImportWorld(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            [selector], []);

        AssertSuccess(result);
        ManagedMethodIdentityV1 identity = Assert.Single(result.Methods).Identity;
        Assert.Equal(token, identity.MetadataToken);
        Assert.Contains("System.Int32", identity.CanonicalSignature, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedCallIr_IsExplicitAndCannotReachBundleEmissionBeforePhase03()
    {
        ManagedCallGraphCompilationV1 result = Import(nameof(ManagedCallGraphFixtures.Root));
        AssertSuccess(result);
        IrProgram caller = Assert.IsType<IrProgram>(result.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.Root)).Import.Program);
        IrInstruction call = Assert.Single(caller.Instructions, static instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        Assert.Equal("JALR", call.Opcode.ToString());
        Assert.NotNull(call.Annotation.BranchTargetSymbolName);
        Assert.True(call.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call));
        Assert.Contains("backend.managed-call-lowering-required/v1", caller.Contract.RequiredCapabilities);

        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(caller);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new HybridCpuBundleLowerer().LowerProgram(bundles));
        Assert.Contains("final ABI/clobber/frame lowering", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedCallMapping_PreservesOrdinaryBranchTargetsAndCurrentCfgEvidence()
    {
        ManagedCallGraphCompilationV1 result = Import(nameof(ManagedCallGraphFixtures.BranchCallRoot));
        AssertSuccess(result);
        ManagedCompiledMethodV1 root = result.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.BranchCallRoot));
        IrProgram program = Assert.IsType<IrProgram>(root.Import.Program);
        IrInstruction branch = Assert.Single(program.Instructions, static instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.ConditionalBranch);
        Assert.NotNull(branch.Annotation.ResolvedBranchTargetInstructionIndex);
        root.Import.ControlFlowAnalysis!.EnsureCurrentFor(program);
    }

    [Theory]
    [InlineData(1, 64, "HCSCF-BUDGET2006")]
    [InlineData(256, 2, "HCSCF-BUDGET2007")]
    public void GraphBudgets_AreDeterministicAndFailBeforeArtifacts(int maximumMethods, int maximumDepth, string code)
    {
        ScalarControlFlowV2Budgets budgets = ScalarControlFlowV2ProfileContractV1.Default.Budgets with
        {
            MaximumReachableMethods = maximumMethods,
            MaximumAcyclicCallDepth = maximumDepth
        };
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            graphBudgets: budgets);
        ManagedBodyWorldV1 world = World(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            [Selector(nameof(ManagedCallGraphFixtures.Root))], []);

        ManagedCallGraphCompilationV1 first = importer.ImportBodyWorld(world);
        ManagedCallGraphCompilationV1 second = importer.ImportBodyWorld(world);
        AssertFailure(first, RestrictedCilImportStatusV1.BudgetExhausted, code);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Empty(first.Methods);
        Assert.Null(first.Graph);
    }

    [Fact]
    public void MetadataResolutionBudget_IsDeterministicAndFailsBeforeArtifacts()
    {
        ScalarControlFlowV2Budgets budgets = ScalarControlFlowV2ProfileContractV1.Default.Budgets with
        {
            MaximumMetadataResolutionSteps = 1
        };
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            graphBudgets: budgets);
        ManagedBodyWorldV1 world = World(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            [Selector(nameof(ManagedCallGraphFixtures.Root))], []);

        AssertFailure(importer.ImportBodyWorld(world), RestrictedCilImportStatusV1.BudgetExhausted,
            "HCSCF-BUDGET2002");
    }

    private static ManagedCallGraphCompilationV1 Import(string root, string sourceIdentity = "phase02-fixture.dll") =>
        ImportWorld(ManagedBodyWorldModeV1.StandaloneRestrictedModule, [Selector(root)], [], sourceIdentity);

    private static ManagedCallGraphCompilationV1 ImportWorld(ManagedBodyWorldModeV1 mode,
        IReadOnlyList<RestrictedCilMethodSelectorV1> roots,
        IReadOnlyList<RestrictedCilMethodSelectorV1> presented,
        string sourceIdentity = "phase02-fixture.dll") =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(World(mode, roots, presented, sourceIdentity));

    private static ManagedBodyWorldV1 World(ManagedBodyWorldModeV1 mode,
        IReadOnlyList<RestrictedCilMethodSelectorV1> roots,
        IReadOnlyList<RestrictedCilMethodSelectorV1> presented,
        string sourceIdentity = "phase02-fixture.dll") =>
        new(mode, FixtureImage, sourceIdentity, roots, presented);

    private static RestrictedCilMethodSelectorV1 Selector(string method) => new(FixtureType, method);
    private static int IndexOf(IReadOnlyList<string> items, string value) => items.ToList().IndexOf(value);

    private static void AssertSuccess(ManagedCallGraphCompilationV1 result)
    {
        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            $"{result.Status}: {string.Join(" | ", result.Diagnostics.Select(static item => $"{item.Code}:{item.Message}:{item.StableSourceIdentity}"))}");
        Assert.NotNull(result.Graph);
        Assert.Empty(result.Diagnostics);
    }

    private static void AssertFailure(ManagedCallGraphCompilationV1 result, RestrictedCilImportStatusV1 status, string code)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Methods);
        Assert.Null(result.Graph);
    }
}

public static class ManagedCallGraphFixtures
{
    public static int Leaf(int value) => value + 1;
    public static int Middle(int value) => Leaf(value) * 2;
    public static int Root(int value) => Middle(value) - 3;
    public static int Shared(int value) => value + 7;
    public static int Left(int value) => Shared(value) + 1;
    public static int Right(int value) => Shared(value) - 1;
    public static int DiamondRoot(int value) => Left(value) + Right(value);
    public static int BranchCallRoot(int value)
        => value == 0 ? Leaf(value) : Shared(value);
    public static int RepeatedRoot(int value) => Shared(value) + Shared(value + 1);
    public static int LoopHelper(int limit)
    {
        int result = 0;
        for (int index = 0; index < limit; index++) result += index;
        return result;
    }
    public static int LoopRoot(int value) => LoopHelper(value) + 1;
    public static int LoopCallingLeafRoot(int limit)
    {
        int result = 0;
        for (int index = 0; index < limit; index++) result += Leaf(index);
        return result;
    }
    public static int ParameterlessRoot() => LoopHelper(5) + Left(3) + Right(3);
    public static int ParameterlessSelfRecursive() => ParameterlessSelfRecursive();
    public static int PrimitiveHelper(int left, int right) => left + right;
    public static int PrimitiveRoot(int value) => PrimitiveHelper(value, 4);
    public static int NineArgumentLeaf(int a, int b, int c, int d, int e, int f, int g, int h, int i)
        => a + b + c + d + e + f + g + h + i;
    public static int NineArgumentRoot(int value)
        => NineArgumentLeaf(value, 1, 2, 3, 4, 5, 6, 7, 8);
    public static int FourArgumentLeaf(int a, int b, int c, int d) => a + b + c + d;
    public static int FourArgumentRoot(int value) => FourArgumentLeaf(value, 1, 2, 3);
    public static int FiveArgumentLeaf(int a, int b, int c, int d, int e) => a + b + c + d + e;
    public static int FiveArgumentRoot(int value) => FiveArgumentLeaf(value, 1, 2, 3, 4);
    public static int PressureRoot(int value)
    {
        int a0 = value + 1;
        int a1 = value + 2;
        int a2 = value + 3;
        int a3 = value + 4;
        int a4 = value + 5;
        int a5 = value + 6;
        int a6 = value + 7;
        int a7 = value + 8;
        int a8 = value + 9;
        int a9 = value + 10;
        int a10 = value + 11;
        int a11 = value + 12;
        int a12 = value + 13;
        int called = Leaf(value);
        return called + a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7 + a8 + a9 + a10 + a11 + a12;
    }
    public static void VoidLeaf(int value) { }
    public static void VoidRoot(int value) => VoidLeaf(value);
    public static void DiscardedCallResultVoidRoot(int value)
    {
        _ = Leaf(value);
    }
    public static void InitPlayerShapeRoot(ControllerFixture controller, int index) => controller.InitPlayer(index);
    public static int ReuseAcrossVoidCallsRoot(int value)
    {
        int held = value + 7;
        VoidLeaf(held);
        VoidLeaf(held);
        return held;
    }
    public static void NoArgumentVoidLeaf() { }
    public static void NoArgumentVoidRoot() => NoArgumentVoidLeaf();
    public static int SelfRecursive(int value) => value == 0 ? 0 : SelfRecursive(value - 1);
    public static int MutualA(int value) => value == 0 ? 0 : MutualB(value - 1);
    public static int MutualB(int value) => value == 0 ? 0 : MutualA(value - 1);
    public static int ExternalCall(int value) => Math.Abs(value);
    public static int GenericIdentity<T>(T value) where T : struct => value.GetHashCode();
    public static int GenericCall(int value) => GenericIdentity(value);
    public static int VirtualCall(int value) => new ScalarVirtual().Apply(value);
    public static int VirtualCallOnExisting(ScalarVirtual target, int value) => target.Apply(value);
    public static decimal UnsupportedSignatureRoot(decimal value) => value;
    public static int Overload(int value) => value + 1;
    public static long Overload(long value) => value + 2;

    public sealed class ScalarVirtual
    {
        public int Apply(int value) => value + 1;
    }

    public sealed class ControllerFixture
    {
        private readonly PlayerFixture[] players = new PlayerFixture[4];

        public void InitPlayer(int index)
        {
            players[index] = new PlayerFixture();
            _ = PlayerReborn(index);
        }

        private PlayerFixture PlayerReborn(int index) => players[index];
    }

    public sealed class PlayerFixture;
}
