using System.Reflection;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Vmcs.V2;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.VmxRefactoring;

public sealed class VmxCompilerIsaRuntimeNoEmissionContractTests
{
    private static readonly InstructionsEnum[] RuntimeVmxOpcodes =
    [
        InstructionsEnum.VMXON,
        InstructionsEnum.VMXOFF,
        InstructionsEnum.VMLAUNCH,
        InstructionsEnum.VMRESUME,
        InstructionsEnum.VMREAD,
        InstructionsEnum.VMWRITE,
        InstructionsEnum.VMCLEAR,
        InstructionsEnum.VMPTRLD,
        InstructionsEnum.VMPTRST,
        InstructionsEnum.VMCALL,
        InstructionsEnum.INVEPT,
        InstructionsEnum.INVVPID,
        InstructionsEnum.VMFUNC,
        InstructionsEnum.VMSAVEX,
        InstructionsEnum.VMRESTX,
    ];

    [Fact]
    public void PublicCompilerFacadeSurfaces_DoNotExposeVmxVmcsOrSecureComputeHelpers()
    {
        string[] publicMemberNames =
        [
            .. PublicMemberNames(typeof(IAppAsmFacade)),
            .. PublicMemberNames(typeof(AppAsmFacade)),
            .. PublicMemberNames(typeof(IPlatformAsmFacade)),
            .. PublicMemberNames(typeof(PlatformAsmFacade)),
            .. PublicMemberNames(typeof(IExpertBackendFacade)),
            .. PublicMemberNames(typeof(ExpertBackendFacade)),
            .. PublicMemberNames(typeof(HybridCpuThreadCompilerContext)),
        ];

        foreach (string forbidden in new[]
                 {
                     "Vmx",
                     "Vmcs",
                     "VmRead",
                     "VmWrite",
                     "VmCall",
                     "VmFunc",
                     "VmLaunch",
                     "VmResume",
                     "VmPtr",
                     "VmxCaps",
                     "SecureCompute",
                     "SecureDomain",
                     "SecureBackend",
                     "BackendExecution",
                     "RuntimeOwnedPublication",
                     "CompletionPublication",
                     "RetirePublication",
                 })
        {
            Assert.DoesNotContain(
                publicMemberNames,
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void VmxCompilerAuthority_RemainsDiagnosticRawTransportOnly()
    {
        IReadOnlyList<CompilerVmxOpcodeAuthority> authority =
            CompilerVmxAuthority.GetRuntimeOpcodeAuthority();

        Assert.Equal(RuntimeVmxOpcodes, authority.Select(static item => item.Opcode).ToArray());

        foreach (CompilerVmxOpcodeAuthority opcodeAuthority in authority)
        {
            Assert.Equal(CompilerVmxAuthorityKind.RuntimeExecutable, opcodeAuthority.Authority);
            Assert.True(opcodeAuthority.RuntimeExecutable);
            Assert.True(opcodeAuthority.RawTransportOnly);
            Assert.True(opcodeAuthority.RootRuntimePolicyGated);
            Assert.False(opcodeAuthority.CompilerHelperEmittable);
            Assert.NotEqual(CompilerVmxAuthorityKind.CompilerEmittable, opcodeAuthority.Authority);
            Assert.True(CompilerVmxAuthority.TryGetOpcodeAuthority(opcodeAuthority.Opcode, out CompilerVmxOpcodeAuthority resolved));
            Assert.Equal(opcodeAuthority, resolved);
        }
    }

    [Fact]
    public void VmxPreflightDiagnostics_DoNotBypassRootPolicyOrTargetCapabilities()
    {
        CompilerVmxTargetCapability allCapabilities =
            CompilerVmxTargetCapability.VmcsV2Revision1 |
            CompilerVmxTargetCapability.NestedVmx |
            CompilerVmxTargetCapability.VectorStreamRestore |
            CompilerVmxTargetCapability.Lane6Restore |
            CompilerVmxTargetCapability.Lane7Restore |
            CompilerVmxTargetCapability.Migration |
            CompilerVmxTargetCapability.DirtyLogging |
            CompilerVmxTargetCapability.ObservabilityExport;

        foreach (CompilerVmxRequestedFeature feature in RequestedFeatures())
        {
            CompilerVmxPreflightResult disabledRoot =
                CompilerVmxAuthority.EvaluatePreflight(
                    new CompilerVmxPreflightRequest(1, feature),
                    allCapabilities,
                    CompilerVmxRootPolicy.Disabled);

            Assert.False(disabledRoot.Succeeded);
            Assert.Equal(CompilerVmxPreflightRejectionKind.RootPolicyDisabled, disabledRoot.RejectionKind);
            Assert.Equal(feature, disabledRoot.RejectedFeature);

            CompilerVmxPreflightResult unsupportedTarget =
                CompilerVmxAuthority.EvaluatePreflight(
                    new CompilerVmxPreflightRequest(1, feature),
                    CompilerVmxTargetCapability.VmcsV2Revision1,
                    AllRootPolicyEnabled);

            Assert.False(unsupportedTarget.Succeeded);
            Assert.Equal(
                CompilerVmxPreflightRejectionKind.UnsupportedTargetCapability,
                unsupportedTarget.RejectionKind);
            Assert.Equal(feature, unsupportedTarget.RejectedFeature);
        }
    }

    [Fact]
    public void VmcsCompilerSidebands_AreValidationOnlyAndNeverAttachToExecutableOpcodes()
    {
        VmcsV2BlockDirectory directory = VmcsV2BlockDirectory.CreateDefault();
        ushort[] fields =
        [
            (ushort)VmcsField.GuestPc,
            VmcsV2BlockDirectory.ShadowVmcsBlockFieldId,
            VmcsV2BlockDirectory.DirtyLogBlockFieldId,
        ];

        foreach (ushort fieldId in fields)
        {
            Assert.True(directory.TryGetField(fieldId, out VmcsV2FieldDescriptor descriptor));
            Assert.True(CompilerVmxAuthority.TryCreateVmcsV2ValidationSideband(
                vmcsV2Revision: 1,
                descriptor,
                out CompilerVmcsV2DescriptorSideband sideband,
                out string diagnostic));
            Assert.Equal(string.Empty, diagnostic);
            Assert.True(sideband.ValidationOnly);
            Assert.False(sideband.CanAttachToExecutableCompilerInstruction);

            foreach (InstructionsEnum opcode in ExecutableOpcodeAttachmentAttempts())
            {
                Assert.False(CompilerVmxAuthority.CanAttachVmcsV2SidebandToOpcode(
                    opcode,
                    sideband,
                    out string attachDiagnostic));
                Assert.Contains("validation-only", attachDiagnostic, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CompilerEmissionSurfaceAndNonVmxIsa_DoNotEmitVirtualizationActivationOrSecureComputeAuthority()
    {
        string compilerEmissionSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();
        string nonVmxIsaSource = ReadRepositorySources(
            "HybridCPU_ISE/CloseToHSL/Core/ISA/Instructions/NonVmx");
        string combined = compilerEmissionSource + Environment.NewLine + nonVmxIsaSource;

        foreach (string forbidden in new[]
                 {
                     "InstructionsEnum.VMXON",
                     "InstructionsEnum.VMXOFF",
                     "InstructionsEnum.VMLAUNCH",
                     "InstructionsEnum.VMRESUME",
                     "InstructionsEnum.VMPTRLD",
                     "InstructionsEnum.VMPTRST",
                     "InstructionsEnum.VMCLEAR",
                     "InstructionsEnum.VMWRITE",
                     "InstructionsEnum.VMCALL",
                     "InstructionsEnum.VMFUNC",
                     "VMXON",
                     "VMLAUNCH",
                     "VMRESUME",
                     "VMWRITE",
                     "VMCALL",
                     "SecureComputeDomainDescriptor",
                     "SecureBackendOwnerAdmissionPolicy",
                     "VmxCaps.Secure",
                     "VmcsManager",
                     "IVmcsManager",
                     "VmxExecutionUnit",
                     "BackendExecutionAuthorized: true",
                     "TrapCompletionRouteDescriptor.RuntimeOwnedPublication",
                     "CompletionRecord.FromCompatibilityExit",
                     "CompletionRecord.TryFromCompatibilityExit",
                     "VmxRetireEffect.InterceptExit",
                     "VmxRetireEffect.VmCall",
                     "VmxRetireEffect.VmFunc",
                 })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompilerSidebandSource_GuardsMetadataTransportWithoutRuntimeAuthority()
    {
        string threadIngress = ReadRepositorySources(
            "Compilers/HybridCPU_Compiler/API/Threading/HybridCpuThreadCompilerContext.cs");
        string irBuilder = ReadRepositorySources(
            "Compilers/HybridCPU_Compiler/Core/IR/Construction/HybridCpuIrBuilder.cs");
        string acceleratorModel = ReadRepositorySources(
            "Compilers/HybridCPU_Compiler/Core/IR/Model/IrAcceleratorModels.cs");

        Assert.Contains(
            "DmaStreamCompute compiler emission requires an accepted owner/domain guard decision before descriptor emission.",
            threadIngress);
        Assert.Contains(
            "L7-SDC compiler emission requires guard-backed owner/domain acceptance before ACCEL_SUBMIT emission.",
            threadIngress);
        Assert.Contains(
            "DmaStreamCompute descriptor sideband may only accompany the native lane6 DmaStreamCompute compiler contour.",
            irBuilder);
        Assert.Contains(
            "Compiler L7-SDC emission requires explicit accelerator intent and typed ACCEL_SUBMIT descriptor sideband before native opcode emission.",
            irBuilder);
        Assert.Contains(
            "runtime authority and not a fallback promise after ACCEL_SUBMIT exists",
            acceleratorModel);

        string combined = threadIngress + Environment.NewLine + irBuilder + Environment.NewLine + acceleratorModel;
        Assert.DoesNotContain("RuntimeOwnedPublication", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("BackendExecutionAuthorized: true", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureCompute", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("VmcsManager", combined, StringComparison.Ordinal);
    }

    private static CompilerVmxRootPolicy AllRootPolicyEnabled => new(
        NestedVmxEnabled: true,
        VectorStreamRestoreEnabled: true,
        Lane6RestoreEnabled: true,
        Lane7RestoreEnabled: true,
        MigrationEnabled: true,
        DirtyLoggingEnabled: true,
        ObservabilityExportEnabled: true);

    private static IEnumerable<CompilerVmxRequestedFeature> RequestedFeatures() =>
        Enum.GetValues<CompilerVmxRequestedFeature>()
            .Where(static feature => feature != CompilerVmxRequestedFeature.None);

    private static InstructionsEnum[] ExecutableOpcodeAttachmentAttempts() =>
    [
        InstructionsEnum.ADD,
        InstructionsEnum.DmaStreamCompute,
        InstructionsEnum.ACCEL_SUBMIT,
        InstructionsEnum.VMREAD,
        InstructionsEnum.VMWRITE,
        InstructionsEnum.VMCALL,
        InstructionsEnum.VMFUNC,
    ];

    private static string[] PublicMemberNames(Type type) =>
    [
        .. type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name),
        .. type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name),
    ];

    private static string ReadRepositorySources(params string[] relativeRoots)
    {
        string repositoryRoot = CompatFreezeScanner.FindRepoRoot();
        return string.Concat(relativeRoots.SelectMany(relativeRoot =>
        {
            string root = Path.Combine(
                repositoryRoot,
                relativeRoot.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(root))
            {
                return new[] { File.ReadAllText(root) };
            }

            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Missing repository source root: {relativeRoot}");
            }

            return Directory
                .GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(static path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText);
        }));
    }
}
