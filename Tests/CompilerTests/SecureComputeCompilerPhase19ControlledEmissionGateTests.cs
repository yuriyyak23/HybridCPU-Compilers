using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class SecureComputeCompilerPhase19ControlledEmissionGateTests
{
    [Fact]
    public void NoCompilerChangeDecision_PreservesNoEmissionAndCreatesNoAuthority()
    {
        SecureComputeControlledEmissionResult result =
            Policy.Classify(new SecureComputeControlledEmissionRequest(
                SecureComputeCompilerEmissionPath.NoCompilerChange,
                RequestsCompilerEmission: false));

        Assert.True(result.IsAllowed);
        Assert.False(result.CompilerEmissionAuthorized);
        Assert.False(result.BackendExecutionAuthorized);
        Assert.False(result.NewInstructionEncodingAuthorized);
        Assert.False(result.NewOperandFormatAuthorized);
        Assert.False(result.CapabilityAwareLoadStoreFetchAuthorized);
        Assert.False(result.VmxSecureModeEmissionAuthorized);
        Assert.False(result.CreatesAnyEmissionAuthority);
    }

    [Theory]
    [InlineData(SecureComputeCompilerEmissionPath.SecureBackendHelper)]
    [InlineData(SecureComputeCompilerEmissionPath.SecureHypercallHelper)]
    [InlineData(SecureComputeCompilerEmissionPath.SecureSidebandMetadata)]
    [InlineData(SecureComputeCompilerEmissionPath.FutureControlledEmission)]
    public void CompilerEmissionRequest_RequiresPositiveRuntimeOwnerFirst(
        SecureComputeCompilerEmissionPath path)
    {
        SecureComputeControlledEmissionResult result =
            Policy.Classify(new SecureComputeControlledEmissionRequest(
                path,
                RequestsCompilerEmission: true));

        Assert.Equal(
            SecureComputeControlledEmissionDecision.DeniedMissingPositiveRuntimeOwner,
            result.Decision);
        Assert.False(result.CreatesAnyEmissionAuthority);
    }

    [Fact]
    public void ControlledEmissionRequest_RemainsFutureGatedEvenWithPrerequisiteFlags()
    {
        SecureComputeControlledEmissionResult missingRfc =
            Policy.Classify(new SecureComputeControlledEmissionRequest(
                SecureComputeCompilerEmissionPath.FutureControlledEmission,
                RequestsCompilerEmission: true,
                HasPositiveNeutralRuntimeOwner: true));
        SecureComputeControlledEmissionResult missingRelease =
            Policy.Classify(new SecureComputeControlledEmissionRequest(
                SecureComputeCompilerEmissionPath.FutureControlledEmission,
                RequestsCompilerEmission: true,
                HasPositiveNeutralRuntimeOwner: true,
                HasControlledEmissionRfc: true));
        SecureComputeControlledEmissionResult backendClosed =
            Policy.Classify(new SecureComputeControlledEmissionRequest(
                SecureComputeCompilerEmissionPath.FutureControlledEmission,
                RequestsCompilerEmission: true,
                HasPositiveNeutralRuntimeOwner: true,
                HasControlledEmissionRfc: true,
                HasReleaseApproval: true));
        SecureComputeControlledEmissionResult futureGated =
            Policy.Classify(new SecureComputeControlledEmissionRequest(
                SecureComputeCompilerEmissionPath.FutureControlledEmission,
                RequestsCompilerEmission: true,
                HasPositiveNeutralRuntimeOwner: true,
                HasControlledEmissionRfc: true,
                HasReleaseApproval: true,
                BackendExecutionAuthorized: true));

        Assert.Equal(SecureComputeControlledEmissionDecision.DeniedMissingControlledEmissionRfc, missingRfc.Decision);
        Assert.Equal(SecureComputeControlledEmissionDecision.DeniedMissingReleaseApproval, missingRelease.Decision);
        Assert.Equal(SecureComputeControlledEmissionDecision.DeniedBackendExecutionClosed, backendClosed.Decision);
        Assert.Equal(SecureComputeControlledEmissionDecision.DeniedCompilerEmissionFutureGated, futureGated.Decision);
        Assert.False(futureGated.CreatesAnyEmissionAuthority);
    }

    [Theory]
    [MemberData(nameof(NoEmissionViolationCases))]
    public void NoEmissionViolationInputs_AreDeniedBeforeControlledEmission(
        SecureComputeControlledEmissionRequest request,
        SecureComputeControlledEmissionDecision expectedDecision)
    {
        SecureComputeControlledEmissionResult result = Policy.Classify(request);

        Assert.Equal(expectedDecision, result.Decision);
        Assert.False(result.CreatesAnyEmissionAuthority);
    }

    [Fact]
    public void CompilerNoEmissionContract_RemainsClosedForSecureComputeShortcuts()
    {
        SecureComputeNoEmissionContract contract = new SecureComputeNoEmissionContract();

        Assert.Equal(
            SecureComputeNoEmissionViolation.NewInstructionEncoding,
            contract.Validate(
                emitsNewInstructionEncoding: true,
                emitsNewOperandFormat: false,
                emitsCapabilityAwareLoadStoreFetch: false,
                emitsVmxSecureMode: false));
        Assert.Equal(
            SecureComputeNoEmissionViolation.NewOperandFormat,
            contract.Validate(
                emitsNewInstructionEncoding: false,
                emitsNewOperandFormat: true,
                emitsCapabilityAwareLoadStoreFetch: false,
                emitsVmxSecureMode: false));
        Assert.Equal(
            SecureComputeNoEmissionViolation.CapabilityAwareLoadStoreFetch,
            contract.Validate(
                emitsNewInstructionEncoding: false,
                emitsNewOperandFormat: false,
                emitsCapabilityAwareLoadStoreFetch: true,
                emitsVmxSecureMode: false));
        Assert.Equal(
            SecureComputeNoEmissionViolation.VmxSecureModeEmission,
            contract.Validate(
                emitsNewInstructionEncoding: false,
                emitsNewOperandFormat: false,
                emitsCapabilityAwareLoadStoreFetch: false,
                emitsVmxSecureMode: true));
    }

    [Fact]
    public void CompilerGateSources_DoNotCreateSecureCompilerEmissionShortcut()
    {
        string source = ReadProjectSource(
            "HybridCPU_ISE/CloseToHSL/Core/Virtualization/SecureCompute/Conformance/NoEmission/SecureComputeNoEmissionContract.cs",
            "HybridCPU_ISE/CloseToHSL/Core/Virtualization/SecureCompute/Conformance/NoEmission/SecureComputeControlledEmissionGatePolicy.cs");
        string compilerSource = ReadProjectSource(
            "Compilers/HybridCPU_Compiler/API/Threading/HybridCpuThreadCompilerContext.cs",
            "Compilers/HybridCPU_Compiler/Core/Compilation/HybridCpuCanonicalCompiler.cs",
            "Compilers/HybridCPU_Compiler/API/Multithreaded/HybridCpuMultithreadedCompiler.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Construction/HybridCpuIrBuilder.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Model/CompilerBackendLoweringContract.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Model/CompilerLane6DeferredAbiContract.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Model/CompilerLane7DeferredAbiContract.cs");

        Assert.Contains("DeniedCompilerEmissionFutureGated", source);
        Assert.Contains("DeniedMissingPositiveRuntimeOwner", source);
        Assert.Contains("DeniedMissingControlledEmissionRfc", source);
        Assert.Contains("DeniedMissingReleaseApproval", source);
        Assert.Contains("DeniedBackendExecutionClosed", source);
        Assert.Contains("CompilerEmissionAuthorized: false", source);
        Assert.Contains("BackendExecutionAuthorized: false", source);
        Assert.Contains("NewInstructionEncodingAuthorized: false", source);
        Assert.Contains("NewOperandFormatAuthorized: false", source);
        Assert.Contains("CapabilityAwareLoadStoreFetchAuthorized: false", source);
        Assert.Contains("VmxSecureModeEmissionAuthorized: false", source);
        Assert.DoesNotContain("CompilerEmissionAuthorized: true", source);
        Assert.DoesNotContain("BackendExecutionAuthorized: true", source);
        Assert.DoesNotContain("NewInstructionEncodingAuthorized: true", source);
        Assert.DoesNotContain("NewOperandFormatAuthorized: true", source);
        Assert.DoesNotContain("CapabilityAwareLoadStoreFetchAuthorized: true", source);
        Assert.DoesNotContain("VmxSecureModeEmissionAuthorized: true", source);
        Assert.DoesNotContain("SecureBackendExecutionRequest", source);
        Assert.DoesNotContain("SecureBackendExecutionDecision", source);

        foreach (string forbidden in ForbiddenCompilerEmissionMarkers())
        {
            Assert.DoesNotContain(forbidden, compilerSource);
        }
    }

    public static IEnumerable<object[]> NoEmissionViolationCases()
    {
        SecureComputeCompilerEmissionPath path =
            SecureComputeCompilerEmissionPath.FutureControlledEmission;

        yield return new object[]
        {
            new SecureComputeControlledEmissionRequest(path, true, EmitsNewInstructionEncoding: true),
            SecureComputeControlledEmissionDecision.DeniedNewInstructionEncoding,
        };
        yield return new object[]
        {
            new SecureComputeControlledEmissionRequest(path, true, EmitsNewOperandFormat: true),
            SecureComputeControlledEmissionDecision.DeniedNewOperandFormat,
        };
        yield return new object[]
        {
            new SecureComputeControlledEmissionRequest(path, true, EmitsCapabilityAwareLoadStoreFetch: true),
            SecureComputeControlledEmissionDecision.DeniedCapabilityAwareLoadStoreFetch,
        };
        yield return new object[]
        {
            new SecureComputeControlledEmissionRequest(path, true, EmitsVmxSecureMode: true),
            SecureComputeControlledEmissionDecision.DeniedVmxSecureModeEmission,
        };
    }

    private static string[] ForbiddenCompilerEmissionMarkers() =>
    [
        "SecureComputeControlledEmission",
        "SecureComputeEmit",
        "EmitSecureCompute",
        "SecureBackendOpcode",
        "SecureBackendHelper",
        "SecureHypercallHelper",
        "CompilerEmissionAuthorized: true",
        "BackendExecutionAuthorized: true",
        "CompletionPublicationAuthorized: true",
        "RetirePublicationAuthorized: true",
        "capability-aware LOAD",
        "capability-aware STORE",
        "capability-aware FETCH",
        "capability register",
        "tagged memory",
    ];

    private static SecureComputeControlledEmissionGatePolicy Policy =>
        SecureComputeControlledEmissionGatePolicy.FailClosed;

    private static string ReadProjectSource(params string[] relativePaths)
    {
        string root = HybridCPU_ISE.Tests.TestHelpers.CompatFreezeScanner.FindRepoRoot();
        string compilerRoot = HybridCPU_ISE.Tests.TestHelpers.CompatFreezeScanner.FindCompilerRepoRoot();
        return string.Concat(relativePaths.Select(path => File.ReadAllText(Path.Combine(
            path.StartsWith("Compilers/", StringComparison.Ordinal) ? compilerRoot : root,
            path.Replace('/', Path.DirectorySeparatorChar)))));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HybridCPU_ISE.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "HybridCPU_ISE", "HybridCPU_ISE.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate HybridCPU ISE repository root.");
    }
}
