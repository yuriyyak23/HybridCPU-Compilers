using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.Hwir;

if (args.Length != 3) throw new ArgumentException("Usage: StageAwareFaultWinner.Runner <active-ise-root> <golden-ise-root> <new-output-directory>");
string activeRoot = Path.GetFullPath(args[0]), goldenRoot = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (!Directory.Exists(activeRoot) || !Directory.Exists(goldenRoot) || Directory.Exists(output))
    throw new InvalidOperationException("Source roots must exist and output must be new.");
const string relative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs";
const string stageEnumRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.PipelineStage.cs";
const string exceptionRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Faults/CPU_Core.PipelineExecution.Exceptions.cs";
const string writeBackLaneRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/WriteBack/CPU_Core.Pipeline.Stages.ScalarWriteBackLaneState.cs";
const string memoryLaneRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Memory/CPU_Core.Pipeline.Stages.ScalarMemoryLaneState.cs";
const string executeLaneRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Execute/CPU_Core.Pipeline.Stages.ScalarExecuteLaneState.cs";
const string translationFaultRelative = "HybridCPU_ISE/CloseToHSL/Core/Runtime/Memory/Translation/CpuInstructionTranslationContour.cs";
const string cpuCoreStateRelative = "HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.StateData.cs";
byte[] active = File.ReadAllBytes(Path.Combine(activeRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
byte[] golden = File.ReadAllBytes(Path.Combine(goldenRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeStageEnum = File.ReadAllBytes(Path.Combine(activeRoot, stageEnumRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenStageEnum = File.ReadAllBytes(Path.Combine(goldenRoot, stageEnumRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeExceptions = File.ReadAllBytes(Path.Combine(activeRoot, exceptionRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenExceptions = File.ReadAllBytes(Path.Combine(goldenRoot, exceptionRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeWriteBackLane = File.ReadAllBytes(Path.Combine(activeRoot, writeBackLaneRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenWriteBackLane = File.ReadAllBytes(Path.Combine(goldenRoot, writeBackLaneRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeMemoryLane = File.ReadAllBytes(Path.Combine(activeRoot, memoryLaneRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenMemoryLane = File.ReadAllBytes(Path.Combine(goldenRoot, memoryLaneRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeExecuteLane = File.ReadAllBytes(Path.Combine(activeRoot, executeLaneRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenExecuteLane = File.ReadAllBytes(Path.Combine(goldenRoot, executeLaneRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeTranslationFault = File.ReadAllBytes(Path.Combine(activeRoot, translationFaultRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenTranslationFault = File.ReadAllBytes(Path.Combine(goldenRoot, translationFaultRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeCpuCoreState = File.ReadAllBytes(Path.Combine(activeRoot, cpuCoreStateRelative.Replace('/', Path.DirectorySeparatorChar)));
byte[] goldenCpuCoreState = File.ReadAllBytes(Path.Combine(goldenRoot, cpuCoreStateRelative.Replace('/', Path.DirectorySeparatorChar)));
string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirStageAwareFaultWinner.Compile(active, golden, activeStageEnum, goldenStageEnum, toolchain);
HwValidatedDesign payloadDesign = BridgeHwirFaultWinnerPayloadMux.Compile(active, golden, activeStageEnum, goldenStageEnum,
    activeWriteBackLane, goldenWriteBackLane, activeMemoryLane, goldenMemoryLane, activeExecuteLane, goldenExecuteLane,
    activeTranslationFault, goldenTranslationFault, activeCpuCoreState, goldenCpuCoreState, toolchain);
HwValidatedDesign deliveryDesign = BridgeHwirPageFaultDeliveryProjection.Compile(
    activeExceptions, active, activeStageEnum, goldenExceptions, golden, goldenStageEnum, toolchain);
HwValidatedDesign composed = BridgeHwirPageFaultDeliveryComposition.Compose(design, payloadDesign, deliveryDesign);
byte[] hwir = new CanonicalHwirSerializer().Serialize(design);
HardwareMlirModule mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(design,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
byte[] composedHwir = new CanonicalHwirSerializer().Serialize(composed);
HardwareMlirModule composedMlir = new HierarchicalCirctMlirLowerer().LowerToMlir(composed,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
Directory.CreateDirectory(output);
File.WriteAllBytes(Path.Combine(output, "stage-aware-fault-winner.hwir.json"), hwir);
File.WriteAllText(Path.Combine(output, "stage-aware-fault-winner.mlir"), mlir.Text, new UTF8Encoding(false));
File.WriteAllBytes(Path.Combine(output, "processor-pagefault-delivery.hwir.json"), composedHwir);
File.WriteAllText(Path.Combine(output, "processor-pagefault-delivery.mlir"), composedMlir.Text, new UTF8Encoding(false));
byte[] payloadHwir = new CanonicalHwirSerializer().Serialize(payloadDesign);
HardwareMlirModule payloadMlir = new HierarchicalCirctMlirLowerer().LowerToMlir(payloadDesign,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
File.WriteAllBytes(Path.Combine(output, "fault-winner-payload-mux.hwir.json"), payloadHwir);
File.WriteAllText(Path.Combine(output, "fault-winner-payload-mux.mlir"), payloadMlir.Text, new UTF8Encoding(false));
var manifest = new
{
    schema = "circuitweave.processor-stage-aware-fault-winner-hwir/v1",
    producerHead = "e4e2faeda14184ed9832f3bdab70bcdf9aeb0fad",
    modelCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    goldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9",
    toolchain = new { sdk = "11.0.100", languageVersion = "15.0", profile = "hwp-hwir-comb-v1", backend = "circt-cli" },
    inputs = new object[]
    {
        new { path = relative, activeSha256 = Sha(active), goldenMaterializedSha256 = Sha(golden), goldenGitBlob = "c17ea0c5930abbf9002868be225b68291ecb446d" },
        new { path = stageEnumRelative, activeSha256 = Sha(activeStageEnum), goldenMaterializedSha256 = Sha(goldenStageEnum), goldenGitBlob = "771660b2c55167e12fc7b8fa2a4393353e20cb4a" },
        new { path = exceptionRelative, activeSha256 = Sha(activeExceptions), goldenMaterializedSha256 = Sha(goldenExceptions), goldenGitBlob = "d4bdff84b4c634bfc41ab34289bec80bd48d62af" },
        new { path = writeBackLaneRelative, activeSha256 = Sha(activeWriteBackLane), goldenMaterializedSha256 = Sha(goldenWriteBackLane), goldenGitBlob = "e539fc5f0647b07786a1fba7fe45e5f2f96c137c" },
        new { path = memoryLaneRelative, activeSha256 = Sha(activeMemoryLane), goldenMaterializedSha256 = Sha(goldenMemoryLane), goldenGitBlob = "706966afb73073e0bc65c6c3b57e47d8719b429c" },
        new { path = executeLaneRelative, activeSha256 = Sha(activeExecuteLane), goldenMaterializedSha256 = Sha(goldenExecuteLane), goldenGitBlob = "f27d8c8798d9416adcb3cf534f179964f6bdf746" },
        new { path = translationFaultRelative, activeSha256 = Sha(activeTranslationFault), goldenMaterializedSha256 = Sha(goldenTranslationFault), goldenGitBlob = "359c6ad30dd204e0894c6c046faa9629778c7dc5" }
        ,new { path = cpuCoreStateRelative, activeSha256 = Sha(activeCpuCoreState), goldenMaterializedSha256 = Sha(goldenCpuCoreState), goldenGitBlob = "6e90e6f8e2dd93c98c0a83aed7039e6a7d869fd0" }
    },
    hwirSha256 = Sha(hwir), mlirSha256 = Sha(Encoding.UTF8.GetBytes(mlir.Text)),
    moduleCount = design.Design.Modules.Count, nodeCount = design.Design.Modules.Sum(m => m.Nodes.Count),
    payloadMuxSha256 = Sha(payloadHwir), payloadMuxMlirSha256 = Sha(Encoding.UTF8.GetBytes(payloadMlir.Text)),
    composedHwirSha256 = Sha(composedHwir), composedMlirSha256 = Sha(Encoding.UTF8.GetBytes(composedMlir.Text)),
    composedModuleCount = composed.Design.Modules.Count, composedNodeCount = composed.Design.Modules.Sum(m => m.Nodes.Count),
    trust = "StructuralOnly",
    translationFaultPayload = new
    {
        fields = new[] { "Reason", "Request.Scope.DomainId", "Request.Scope.ContextId", "Request.Scope.VirtualThreadId",
            "Request.AccessKind", "Request.VirtualAddress", "Request.AccessSize", "Request.OperationIdentity.AttemptId",
            "Request.OperationIdentity.EventId", "Request.OperationIdentity.MemoryOperationId", "OwnerEpoch", "Qualification",
            "Auxiliary", "FaultStage", "HasGuestPhysicalAddress", "GuestPhysicalAddress", "MemoryDomainOwnerEpoch",
            "AddressSpaceGeneration", "AddressSpaceIdentity", "FailureOffset", "TranslationGeneration",
            "RequestStartGuestPhysicalAddress (presence + value)" },
        sharedActiveGoldenPositionalFields = true,
        activeOnlyFields = new[] { "FailureOffset", "TranslationGeneration", "RequestStartGuestPhysicalAddress" },
        nullableTags = new[] { "CpuInstructionTranslationFault.HasValue", "RequestStartGuestPhysicalAddress.HasValue" },
        derivedSignalsElaborated = new[] { "CpuInstructionTranslationFault.IsValid (active source formula; active-only FailureOffset bound)",
            "FailingVirtualAddress wrapped sum modulo-2^64", "checked FailingVirtualAddress overflow flag" },
        smtWays = new { activeValue = 4, goldenValue = 4, activeSha256 = Sha(activeCpuCoreState), goldenSha256 = Sha(goldenCpuCoreState) },
        activeGoldenSemanticDifference = "Golden IsValid omits active-only FailureOffset < Request.AccessSize; derived IsValid follows active source and is not claimed Golden-equivalent for out-of-range offsets.",
        semanticRulesNotElaborated = new[] { "stale-generation/replay behavior", "managed checked-overflow exception side effect" }
    },
    contract = "Hierarchical combinational top: lane-state winner resolver; stage/lane muxes for fault address, write, owner thread, virtual thread, PC, nullable translation-fault presence and every positional/init scalar field of CpuInstructionTranslationFault, including nested Scope/Request/OperationIdentity; active-source IsValid formula with pinned SmtWays=4; modulo-2^64 failing VA and checked-overflow flag; plus MEM PageFault delivery. Active-only translation fields remain versioned and are absent from Golden; out-of-range failure offsets are not Golden-equivalent. No stale/replay behavior, managed exception side effect, cycle state, reset/stall, architectural CSR/mtvec entry or equivalence claim."
};
File.WriteAllText(Path.Combine(output, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest,
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(manifest));
