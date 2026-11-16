#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Combinational projection of the active VMX DMA validation freshness guard.</summary>
public static class BridgeHwirVmxValidationCurrent
{
    public const string SourceSha256 = "ea13437d8f7b343e8bcd72d66a06c0214c416ae73270bf9a96f69daffa273252";

    public static HwValidatedDesign Compile(byte[] sourceBytes, string baseCommit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (Hash(sourceBytes) != SourceSha256)
            throw new InvalidDataException("VMX DMA validation source bytes changed.");
        if (baseCommit.Length != 40 || baseCommit.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned source commit and combinational profile required.");

        var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(sourceBytes).TrimStart('\uFEFF'));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("Invalid pinned VMX DMA validator syntax.");
        MethodDeclarationSyntax method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "IsValidationCurrent");
        string body = method.ToString();
        string[] requiredFragments =
        [
            "!evidence.IsComplete", "DmaFaultKind.ValidationEvidenceMissing",
            "evidence.IoDomainTag", "evidence.DomainTag", "evidence.DeviceId",
            "binding.DomainId != evidence.DomainId", "binding.DomainEpoch != evidence.DomainEpoch",
            "IOMMU.IotlbMappingEpoch != evidence.IotlbMappingEpoch",
            "DmaFaultKind.ValidationEvidenceStale", "return true;"
        ];
        if (requiredFragments.Any(x => !body.Contains(x, StringComparison.Ordinal)))
            throw new InvalidDataException("VMX DMA freshness method no longer matches the guarded source shape.");

        var line = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/VmxDmaDescriptorValidator.cs",
            "CircuitWeave.VmxValidationCurrent", "YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute.VmxDmaDescriptorValidator",
            "IsValidationCurrent", Hash(sourceBytes), HwSourceRole.Primary, line.Line + 1, line.Character + 1);
        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        int serial = 0;
        HwBitVectorType T(int width) => new(width);
        string Input(string name, int width) { ports.Add(new(name, HwPortDirection.Input, T(width))); return name; }
        string C(uint value, int width)
        {
            string id = "k" + serial++;
            nodes.Add(new HwConstantNode(id, T(width), Convert.ToString(value, 2).PadLeft(width, '0')[^width..], origin));
            return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string id = "n" + serial++;
            nodes.Add(new HwOperationNode(id, opcode, args, T(width), Origin: origin));
            return id;
        }

        string complete = Input("evidence_complete", 1);
        // Lookup key used by IoVirtualizationBlock.TryResolveBinding. The associative
        // lookup/result remains an explicit input boundary in this projection.
        _ = Input("evidence_io_domain_tag", 16);
        _ = Input("evidence_domain_tag", 64);
        _ = Input("evidence_device_id", 32);
        string bindingFound = Input("binding_found", 1);
        string suppliedDomain = Input("evidence_domain_id", 32);
        string resolvedDomain = Input("binding_domain_id", 32);
        string suppliedDomainEpoch = Input("evidence_domain_epoch", 64);
        string resolvedDomainEpoch = Input("binding_domain_epoch", 64);
        string suppliedIotlbEpoch = Input("evidence_iotlb_epoch", 64);
        string currentIotlbEpoch = Input("current_iotlb_epoch", 64);
        string descriptorAddress = Input("descriptor_address", 64);
        string domainMatch = Op("eq", 1, suppliedDomain, resolvedDomain);
        string domainEpochMatch = Op("eq", 1, suppliedDomainEpoch, resolvedDomainEpoch);
        string iotlbEpochMatch = Op("eq", 1, suppliedIotlbEpoch, currentIotlbEpoch);
        string fresh = Op("and", 1, bindingFound, Op("and", 1, domainMatch, Op("and", 1, domainEpochMatch, iotlbEpochMatch)));
        string missing = Op("eq", 1, complete, C(0, 1));
        string stale = Op("and", 1, Op("eq", 1, missing, C(0, 1)), Op("eq", 1, fresh, C(0, 1)));
        string fault = Op("or", 1, missing, stale);
        string current = Op("eq", 1, fault, C(0, 1));
        // Values are the active DmaFaultKind enum values, not local status codes.
        string faultKind = Op("mux", 32, missing, C(9, 32), Op("mux", 32, stale, C(10, 32), C(0, 32)));
        void Output(string name, int width, string value)
        { ports.Add(new(name, HwPortDirection.Output, T(width))); nodes.Add(new HwOutputDriveNode("drive_" + name, name, value, origin)); }
        Output("validation_current", 1, current);
        Output("fault", 1, fault);
        Output("fault_kind", 32, faultKind); // ValidationEvidenceMissing=9, ValidationEvidenceStale=10
        Output("fault_disposition", 32, Op("and", 32, C(1, 32), Op("mux", 32, fault, C(uint.MaxValue, 32), C(0, 32)))); // Abort=1
        Output("fault_address", 64, Op("and", 64, descriptorAddress, Op("mux", 64, fault, C(uint.MaxValue, 64), C(0, 64))));
        Output("fault_is_write", 1, C(0, 1));

        const string moduleName = "vmx_validation_current";
        var module = new HwModule(moduleName, moduleName, ports, nodes, [],
            new HwHardwareContract("vmx.dma.validation-current.source-projection.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("VMX DMA validation-current HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(x => x.Code + " " + x.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
