using System.Runtime.InteropServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Tests.TestHelpers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase13ManagedInteropTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(Phase13PInvokeCorpus).Assembly.Location);
    private static readonly HybridCpuManagedTlsLayoutV1 EmptyTls = HybridCpuManagedTlsLayoutV1.Create([]);

    [Fact]
    public void InteropContract_IsClosedDefaultOffAndAddsNoServiceOpcode()
    {
        Assert.Equal("hybridcpu.managed-interop/v1", HybridCpuManagedInteropContractV1.SchemaId);
        Assert.Matches("^[0-9a-f]{64}$", HybridCpuManagedInteropContractV1.ContractDigest);
        Assert.False(HybridCpuManagedInteropOptionsV1.Production.Enabled);
        Assert.False(HybridCpuManagedInteropOptionsV1.Qualification.AllowCallbacks);
        Assert.DoesNotContain(Enum.GetNames<HybridCpuOpcode>(), static name =>
            name.Contains("CONSOLE", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("FILE", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("NETWORK", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PINVOKE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff,
            HybridCpuManagedFeatureSetV1.Default.Workstreams.Single(static row =>
                row.Identity == "managed-unmanaged-interop").Support);
        Assert.DoesNotContain("managed-unmanaged-interop",
            HybridCpuSdkPackContractV1.CreateManifest().PublishQualifiedWorkstreams);
    }

    [Fact]
    public void CheckedInPInvokeMetadata_IsAdmittedAndWrapperLowersToExactDispatchThunk()
    {
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilPInvokeAdmissionV1 first = importer.AdmitPInvokeImage(FixtureImage,
            new(typeof(Phase13PInvokeCorpus).FullName!, nameof(Phase13PInvokeCorpus.NativeAdd)));
        RestrictedCilPInvokeAdmissionV1 second = importer.AdmitPInvokeImage(FixtureImage,
            new(typeof(Phase13PInvokeCorpus).FullName!, nameof(Phase13PInvokeCorpus.NativeAdd)));
        RestrictedCilImportResultV1 wrapper = importer.ImportImage(FixtureImage,
            new(typeof(Phase13PInvokeCorpus).FullName!, nameof(Phase13PInvokeCorpus.AddWrapper)), "phase13-pinvoke");

        Assert.True(first.IsAdmitted, first.Reason);
        Assert.Equal("hc.phase13", first.Signature!.Library);
        Assert.Equal("add_i8", first.Signature.Symbol);
        Assert.Equal(first.AdmissionDigest, second.AdmissionDigest);
        Assert.Equal(first.ImportSymbol, second.ImportSymbol);
        Assert.StartsWith("__hybridcpu_pinvoke_", first.ImportSymbol, StringComparison.Ordinal);
        Assert.Equal(RestrictedCilImportStatusV1.Success, wrapper.Status);
        Assert.Contains(wrapper.Program!.Instructions, static row => row.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
            row.Annotation.BranchTargetSymbolName == HybridCpuManagedInteropLoweringV1.DispatchHelper);
    }

    [Fact]
    public void UnsupportedMetadataAndMarshallingShapes_FailClosed()
    {
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilPInvokeAdmissionV1 managedString = importer.AdmitPInvokeImage(FixtureImage,
            new(typeof(Phase13PInvokeCorpus).FullName!, nameof(Phase13PInvokeCorpus.NativeString)));
        RestrictedCilPInvokeAdmissionV1 lastError = importer.AdmitPInvokeImage(FixtureImage,
            new(typeof(Phase13PInvokeCorpus).FullName!, nameof(Phase13PInvokeCorpus.NativeLastError)));
        HybridCpuManagedInteropSignatureV1 invalidStruct = new HybridCpuManagedInteropSignatureV1("hc.phase13", "bad_struct",
            HybridCpuInteropCallingConventionV1.HybridCpuNativeV2,
            [new(HybridCpuInteropValueKindV1.FixedLayoutStruct, 24, 8)],
            new(HybridCpuInteropValueKindV1.Void, 0, 1));

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, managedString.Status);
        Assert.Contains("managed reference", managedString.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, lastError.Status);
        Assert.Contains("SetLastError", lastError.Reason, StringComparison.Ordinal);
        Assert.False(HybridCpuManagedInteropContractV1.TryValidateSignature(invalidStruct, out _));
    }

    [Fact]
    public void ThunkPlan_UsesNativeAbiForIntegersFpPointersStructsAndExplicitBuffers()
    {
        HybridCpuManagedInteropSignatureV1 signature = Signature("marshal", parameters:
        [
            new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8),
            new(HybridCpuInteropValueKindV1.Float, 8, 8),
            new(HybridCpuInteropValueKindV1.RawPointer, 8, 8),
            new(HybridCpuInteropValueKindV1.FixedLayoutStruct, 16, 8),
            new(HybridCpuInteropValueKindV1.BufferPointer, 8, 8),
            new(HybridCpuInteropValueKindV1.BufferLength, 8, 8)
        ]);
        HybridCpuManagedInteropThunkPlanV1 first = HybridCpuManagedInteropLoweringV1.Plan(signature);
        HybridCpuManagedInteropThunkPlanV1 second = HybridCpuManagedInteropLoweringV1.Plan(signature);

        Assert.True(first.IsSupported, first.Reason);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, first.NativeLayout!.Status);
        Assert.Equal(HybridCpuManagedInteropLoweringV1.DispatchHelper, first.DispatchHelper);
        Assert.Contains("publish-native-call-roots", first.Steps);
        Assert.Contains("contain-native-boundary-exception", first.Steps);
        Assert.Equal(first.PlanDigest, second.PlanDigest);
    }

    [Fact]
    public void KernelHostBoundary_ValidatesContextPrivilegeDigestAndBufferBeforeDispatch()
    {
        DeterministicMockHostServiceProviderV1 provider = new DeterministicMockHostServiceProviderV1();
        Assert.True(provider.Register(HybridCpuHostServiceV1.Console, 7,
            new(HybridCpuExternalServiceStatusV1.Success, 19, 0, string.Empty)));
        (DeterministicRuntimeKernelV1 kernel, _, ulong context) = Runtime(provider);
        HybridCpuExternalServiceRequestV1 valid = Request(context, HybridCpuHostServiceV1.Console, 7,
            0x0020_0100, 16, HybridCpuHostBufferAccessV1.Read);

        Assert.Equal(19UL, kernel.ExternalServiceTransition(valid).ReturnValue);
        Assert.Equal(HybridCpuExternalServiceStatusV1.InvalidRequest,
            kernel.ExternalServiceTransition(valid with { TransitionDigest = new string('0', 64) }).Status);
        HybridCpuExternalServiceRequestV1 supervisor = valid with { PrivilegeMode = HybridCpuPrivilegeModeV1.Supervisor };
        supervisor = supervisor with { TransitionDigest = HybridCpuManagedInteropContractV1.ComputeTransitionDigest(supervisor) };
        Assert.Equal(HybridCpuExternalServiceStatusV1.InvalidRequest,
            kernel.ExternalServiceTransition(supervisor).Status);
        HybridCpuExternalServiceRequestV1 invalidBuffer = Request(context, HybridCpuHostServiceV1.Console, 7,
            0x9000_0000, 16, HybridCpuHostBufferAccessV1.Read);
        Assert.Equal(HybridCpuExternalServiceStatusV1.AccessDenied,
            kernel.ExternalServiceTransition(invalidBuffer).Status);
        Assert.Single(provider.Trace());
    }

    [Fact]
    public void RuntimeTransition_PublishesPinsDefersGcAndReleasesRoots()
    {
        HybridCpuManagedInteropRuntimeV1? runtime = null;
        DelegateProvider provider = new DelegateProvider(request =>
        {
            Assert.NotNull(runtime);
            Assert.True(runtime!.IsInNativeTransition(1));
            Assert.Equal([0x7000UL, 0x8000UL], runtime.NativeCallRoots().Select(static root => root.ObjectReference));
            Assert.Equal(HybridCpuManagedInteropGcRequestV1.DeferredByNativeTransition, runtime.RequestGc(1));
            return new(HybridCpuExternalServiceStatusV1.Success, request.Arguments.Aggregate(0UL, static (a, b) => a + b), 0, string.Empty);
        });
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 threads, _) = Runtime(provider);
        HybridCpuManagedNativeResolverV1 resolver = new HybridCpuManagedNativeResolverV1();
        HybridCpuManagedInteropSignatureV1 signature = Signature("add_i8");
        Assert.True(resolver.Register(signature, HybridCpuHostServiceV1.Console, 1));
        runtime = new(kernel, threads, resolver, HybridCpuManagedInteropOptionsV1.Qualification);

        HybridCpuManagedInteropResultV1 result = runtime.Invoke(new(1, signature, [7, 11],
            PinnedObjectReferences: [0x8000, 0x7000, 0x7000]));

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(18UL, result.ReturnValue);
        Assert.True(result.GcDeferredDuringTransition);
        Assert.Equal([0x7000UL, 0x8000UL], result.NativeCallRoots);
        Assert.Empty(runtime.NativeCallRoots());
        Assert.False(runtime.IsInNativeTransition(1));
    }

    [Fact]
    public void MissingSymbolProviderFailureAndDefaultOff_AreExplicitDeterministicResults()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 threads, _) =
            Runtime(new ThrowingProvider());
        HybridCpuManagedInteropSignatureV1 signature = Signature("missing");
        HybridCpuManagedNativeResolverV1 emptyResolver = new HybridCpuManagedNativeResolverV1();
        HybridCpuManagedInteropRuntimeV1 qualified = new HybridCpuManagedInteropRuntimeV1(kernel, threads, emptyResolver,
            HybridCpuManagedInteropOptionsV1.Qualification);
        HybridCpuManagedInteropResultV1 missing = qualified.Invoke(new(1, signature, []));
        HybridCpuManagedNativeResolverV1 resolver = new HybridCpuManagedNativeResolverV1();
        Assert.True(resolver.Register(signature, HybridCpuHostServiceV1.File, 2));
        HybridCpuManagedInteropRuntimeV1 throwing = new HybridCpuManagedInteropRuntimeV1(kernel, threads, resolver,
            HybridCpuManagedInteropOptionsV1.Qualification);
        HybridCpuManagedInteropResultV1 contained = throwing.Invoke(new(1, signature, []));
        HybridCpuManagedInteropResultV1 disabled = new HybridCpuManagedInteropRuntimeV1(kernel, threads, resolver)
            .Invoke(new(1, signature, []));

        Assert.Equal(HybridCpuExternalServiceStatusV1.MissingSymbol, missing.Status);
        Assert.Equal(HybridCpuExternalServiceStatusV1.ProviderFailure, contained.Status);
        Assert.Contains("exception was contained", contained.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HybridCpuExternalServiceStatusV1.Disabled, disabled.Status);
        Assert.NotEqual(missing.ResultDigest, contained.ResultDigest);
    }

    [Fact]
    public void ReentrantManagedCallback_IsRejectedWithoutCorruptingOuterTransition()
    {
        HybridCpuManagedInteropRuntimeV1? runtime = null;
        HybridCpuManagedInteropInvocationV1? invocation = null;
        HybridCpuManagedInteropResultV1? nested = null;
        DelegateProvider provider = new DelegateProvider(_ =>
        {
            nested = runtime!.Invoke(invocation!);
            return new(HybridCpuExternalServiceStatusV1.Success, 23, 0, string.Empty);
        });
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 threads, _) = Runtime(provider);
        HybridCpuManagedInteropSignatureV1 signature = Signature("callback_attempt");
        HybridCpuManagedNativeResolverV1 resolver = new HybridCpuManagedNativeResolverV1();
        Assert.True(resolver.Register(signature, HybridCpuHostServiceV1.Network, 3));
        runtime = new(kernel, threads, resolver, HybridCpuManagedInteropOptionsV1.Qualification);
        invocation = new(1, signature, []);

        HybridCpuManagedInteropResultV1 outer = runtime.Invoke(invocation);

        Assert.True(outer.IsSuccess);
        Assert.Equal(23UL, outer.ReturnValue);
        Assert.Equal(HybridCpuExternalServiceStatusV1.ReentrancyRejected, nested!.Status);
        Assert.False(runtime.IsInNativeTransition(1));
    }

    [Fact]
    public void MockHostProvider_IsDeterministicAndServiceIdsRemainLibraryPolicy()
    {
        string Run()
        {
            DeterministicMockHostServiceProviderV1 provider = new DeterministicMockHostServiceProviderV1();
            Assert.True(provider.Register(HybridCpuHostServiceV1.Clock, 9,
                new(HybridCpuExternalServiceStatusV1.Success, 1234, 0, "logical-time")));
            (DeterministicRuntimeKernelV1 kernel, _, ulong context) = Runtime(provider);
            HybridCpuExternalServiceResultV1 result = kernel.ExternalServiceTransition(
                Request(context, HybridCpuHostServiceV1.Clock, 9));
            return $"{result.ResultDigest}|{string.Join(';', provider.Trace())}";
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void IseExecutionSources_DoNotAcquireManagedInteropOrHostProviderAuthority()
    {
        string root = CompatFreezeScanner.FindRepoRoot();
        string executionRoot = Path.Combine(root, "HybridCPU_ISE", "CloseToHSL", "Core");
        string[] forbidden =
        [
            "ExternalServiceTransition",
            "IHybridCpuHostServiceProvider",
            "HybridCpuManagedInterop",
            "DllImport",
            "NativeLibrary.",
            "Marshal."
        ];

        foreach (string path in Directory.EnumerateFiles(executionRoot, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            Assert.All(forbidden, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
        }
    }

    private static HybridCpuManagedInteropSignatureV1 Signature(string symbol,
        IReadOnlyList<HybridCpuInteropValueDescriptorV1>? parameters = null) => new(
            "hc.phase13", symbol, HybridCpuInteropCallingConventionV1.HybridCpuNativeV2,
            parameters ?? [new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8),
                new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8)],
            new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8));

    private static (DeterministicRuntimeKernelV1 Kernel, HybridCpuManagedThreadingRuntimeV1 Threads,
        ulong ContextId) Runtime(IHybridCpuHostServiceProviderV1 provider)
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1(provider);
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,
            new string('a', 64), 0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0));
        Assert.True(boot.IsSuccess, boot.Reason);
        HybridCpuManagedThreadingRuntimeV1 threads = new HybridCpuManagedThreadingRuntimeV1(kernel, EmptyTls,
            HybridCpuManagedThreadingOptionsV1.Qualification);
        Assert.True(threads.AttachInitialThread("main").IsSuccess);
        return (kernel, threads, boot.Context!.ContextId);
    }

    private static HybridCpuExternalServiceRequestV1 Request(ulong context,
        HybridCpuHostServiceV1 service, ulong operation, ulong address = 0, ulong length = 0,
        HybridCpuHostBufferAccessV1 access = HybridCpuHostBufferAccessV1.None)
    {
        HybridCpuExternalServiceRequestV1 draft = new HybridCpuExternalServiceRequestV1(context, service, operation,
            HybridCpuPrivilegeModeV1.User, address, length, access, [], string.Empty);
        return draft with { TransitionDigest = HybridCpuManagedInteropContractV1.ComputeTransitionDigest(draft) };
    }

    private sealed class DelegateProvider(
        Func<HybridCpuExternalServiceRequestV1, HybridCpuHostProviderResultV1> invoke)
        : IHybridCpuHostServiceProviderV1
    {
        public HybridCpuHostProviderResultV1 Invoke(HybridCpuExternalServiceRequestV1 request) => invoke(request);
    }

    private sealed class ThrowingProvider : IHybridCpuHostServiceProviderV1
    {
        public HybridCpuHostProviderResultV1 Invoke(HybridCpuExternalServiceRequestV1 request) =>
            throw new InvalidOperationException("contained");
    }
}

public static class Phase13PInvokeCorpus
{
    [DllImport("hc.phase13", EntryPoint = "add_i8", CallingConvention = CallingConvention.Cdecl)]
    public static extern long NativeAdd(long left, long right);

    [DllImport("hc.phase13", EntryPoint = "string", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NativeString(string value);

    [DllImport("hc.phase13", EntryPoint = "last_error", CallingConvention = CallingConvention.Cdecl,
        SetLastError = true)]
    public static extern int NativeLastError(int value);

    public static long AddWrapper(long left, long right) => NativeAdd(left, right);
}
