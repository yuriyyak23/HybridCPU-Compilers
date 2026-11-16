using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;

if (args.Length == 4 && args[0] == "--publish")
{
    await PublishExistingCandidate(args[1], args[2], args[3]);
    return;
}

if (args.Length != 1) throw new ArgumentException("Usage: VectorPodCsr.Runner <existing-output-directory-inside-HardwareBridge>");
string output = Path.GetFullPath(args[0]);
DirectoryInfo? bridgeDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (bridgeDirectory is not null && !File.Exists(Path.Combine(bridgeDirectory.FullName, "HardwareBridge.Companion.csproj"))) bridgeDirectory = bridgeDirectory.Parent;
string bridgeRoot = bridgeDirectory?.FullName ?? throw new InvalidOperationException("HardwareBridge root not found.");
if (!output.StartsWith(bridgeRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");

const string modelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string goldenRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\golden-vector-csr-source";
string activeVector = Path.Combine(modelRoot, "CloseToHSL", "Core", "Architecture", "State", "Architectural", "CPU_Core.VectorConfig.cs");
string activeControl = Path.Combine(modelRoot, "CloseToHSL", "Core", "Pipeline", "MicroOps", "Control", "MicroOp.Control.cs");
string activeState = Path.Combine(modelRoot, "CloseToHSL", "Core", "Architecture", "State", "Architectural", "CPU_Core.State.cs");
string activeRetire = Path.Combine(modelRoot, "CloseToHSL", "Core", "Pipeline", "Retire", "Evidence", "CPU_Core.PipelineExecution.Retire.cs");
string activeDecode = Path.Combine(modelRoot, "CloseToHSL", "Core", "Diagnostics", "InstructionRegistry.Helpers.Csr.cs");
string activeOpcode = Path.Combine(modelRoot, "NonRTL", "Arch", "Generated", "GeneratedIsaOpcodeValues.g.cs");
string activeClassifier = Path.Combine(modelRoot, "NonRTL", "Arch", "InstructionClassifier.cs");
string activeAddresses = Path.Combine(modelRoot, "Arch", "CsrAddresses.cs");
string goldenVector = Path.Combine(goldenRoot, "Core_Architecture_State_Architectural_CPU_Core.VectorConfig.cs");
string goldenControl = Path.Combine(goldenRoot, "Core_Pipeline_MicroOps_Control_MicroOp.Control.cs");
string goldenState = Path.Combine(goldenRoot, "Core_Architecture_State_Architectural_CPU_Core.State.cs");
string goldenRetire = Path.Combine(goldenRoot, "Core_Pipeline_Retire_Evidence_CPU_Core.PipelineExecution.Retire.cs");
string goldenPipelineRoot = Path.Combine(Path.GetDirectoryName(goldenRoot)!, "golden-csr-clear-pipeline-source");
string goldenDecode = Path.Combine(goldenPipelineRoot, "CloseToHSL_Core_Diagnostics_InstructionRegistry.Helpers.Csr.cs");
string goldenOpcode = Path.Combine(goldenPipelineRoot, "NonRTL_Arch_Generated_GeneratedIsaOpcodeValues.g.cs");
string goldenClassifier = Path.Combine(goldenPipelineRoot, "NonRTL_Arch_InstructionClassifier.cs");
string goldenControlPath = Path.Combine(goldenPipelineRoot, "CloseToHSL_Core_Pipeline_MicroOps_Control_MicroOp.Control.cs");
string goldenRetirePath = Path.Combine(goldenPipelineRoot, "CloseToHSL_Core_Pipeline_Retire_Evidence_CPU_Core.PipelineExecution.Retire.cs");
string goldenStatePath = Path.Combine(goldenPipelineRoot, "CloseToHSL_Core_Architecture_State_Architectural_CPU_Core.State.cs");
byte[] Read(string path) => File.ReadAllBytes(path);
byte[] activeVectorBytes = Read(activeVector);
byte[] activeControlBytes = Read(activeControl);
byte[] activeStateBytes = Read(activeState);
byte[] activeRetireBytes = Read(activeRetire);
byte[] activeDecodeBytes = Read(activeDecode);
byte[] activeOpcodeBytes = Read(activeOpcode);
byte[] activeClassifierBytes = Read(activeClassifier);
byte[] activeAddressBytes = Read(activeAddresses);
byte[] goldenVectorBytes = Read(goldenVector);
byte[] goldenControlBytes = Read(goldenControl);
byte[] goldenStateBytes = Read(goldenState);
byte[] goldenRetireBytes = Read(goldenRetire);
byte[] goldenDecodeBytes = Read(goldenDecode);
byte[] goldenOpcodeBytes = Read(goldenOpcode);
byte[] goldenClassifierBytes = Read(goldenClassifier);
byte[] goldenPipelineControlBytes = Read(goldenControlPath);
byte[] goldenPipelineRetireBytes = Read(goldenRetirePath);
byte[] goldenPipelineStateBytes = Read(goldenStatePath);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign vectorCandidate = BridgeHwirVectorPodCsr.Compile(
    activeVectorBytes, activeControlBytes, activeStateBytes, activeAddressBytes,
    goldenVectorBytes, goldenControlBytes, goldenStateBytes,
    BridgeHwirVectorPodCsr.ActiveBaseCommit, BridgeHwirVectorPodCsr.GoldenCommit, toolchain);
HwValidatedDesign retireCandidate = BridgeHwirVectorRetireCounterClear.Compile(
    activeStateBytes, activeControlBytes, activeRetireBytes, goldenStateBytes, goldenControlBytes, goldenRetireBytes, toolchain);
HwValidatedDesign csrClearRetirePath = BridgeHwirCsrClearRetirePath.Compile(
    activeDecodeBytes, activeOpcodeBytes, activeClassifierBytes, activeControlBytes, activeRetireBytes, activeStateBytes,
    goldenDecodeBytes, goldenOpcodeBytes, goldenClassifierBytes, goldenPipelineControlBytes, goldenPipelineRetireBytes, goldenPipelineStateBytes,
    retireCandidate.Design, toolchain);
HwDesign combinedDesign = csrClearRetirePath.Design with
{
    Modules = vectorCandidate.Design.Modules.Concat(csrClearRetirePath.Design.Modules)
        .GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.Single()).ToArray()
};
HwValidationResult combinedValidation = new FailClosedHardwareIrValidator().Validate(combinedDesign);
HwValidatedDesign candidate = combinedValidation.Validated ?? throw new InvalidDataException(
    "Combined Vector CSR/retire-counter HWIR refused: " + string.Join("; ", combinedValidation.Diagnostics.Select(d => d.Code + " " + d.Message)));
byte[] changedSource = activeVectorBytes.ToArray();
changedSource[0] ^= 1;
bool staleSourceRejected = false;
try
{
    _ = BridgeHwirVectorPodCsr.Compile(changedSource, activeControlBytes, activeStateBytes, activeAddressBytes,
        goldenVectorBytes, goldenControlBytes, goldenStateBytes,
        BridgeHwirVectorPodCsr.ActiveBaseCommit, BridgeHwirVectorPodCsr.GoldenCommit, toolchain);
}
catch (InvalidDataException) { staleSourceRejected = true; }
if (!staleSourceRejected) throw new InvalidOperationException("Changed source bytes were not refused.");
HwModule readModule = candidate.Design.Modules.Single(m => m.Id == "vector_pod_csr_read_projection");
HwModule writeModule = candidate.Design.Modules.Single(m => m.Id == "vector_pod_csr_write_projection");
var backendOptions = new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>());
HardwareMlirModule mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(candidate, backendOptions);

Dictionary<string, BigInteger> Evaluate(HwModule module, Dictionary<string, BigInteger> inputValues)
{
    var drivers = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
    var nodes = module.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    BigInteger Mask(int width) => (BigInteger.One << width) - BigInteger.One;
    BigInteger Signal(string id)
    {
        if (inputValues.TryGetValue(id, out BigInteger value)) return value;
        HwNode node = nodes[id];
        value = node switch
        {
            HwConstantNode constant => Convert.ToUInt64(constant.BitPattern, 2),
            HwMuxNode mux => Signal(mux.Condition) != 0 ? Signal(mux.WhenTrue) : Signal(mux.WhenFalse),
            HwOperationNode operation => Apply(operation),
            _ => throw new InvalidDataException("Unexpected node " + node.GetType().Name)
        };
        return inputValues[id] = value;
    }
    BigInteger Apply(HwOperationNode op)
    {
        BigInteger[] x = op.Operands.Select(Signal).ToArray();
        BigInteger y = op.Opcode switch
        {
            "eq" => x[0] == x[1] ? BigInteger.One : BigInteger.Zero,
            "and" => x[0] & x[1],
            "or" => x[0] | x[1],
            "xor" => x[0] ^ x[1],
            "trunc" => x[0],
            "zext" => x[0],
            "shl64_mask6" => x[0] << (int)(x[1] & 63),
            "lshr64_mask6" => x[0] >> (int)(x[1] & 63),
            _ => throw new InvalidDataException("Unsupported operation in vector CSR evaluator: " + op.Opcode)
        };
        return y & Mask(op.ResultType.Width);
    }
    return drivers.ToDictionary(kv => kv.Key, kv => Signal(kv.Value), StringComparer.Ordinal);
}

(long Count, string VectorFile) RunOracleVectors(string path)
{
    long count = 0;
    using var vectors = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    void Check(uint address, bool write, ulong writeValue, byte stateMask, ushort statePriority)
    {
        var inputs = new Dictionary<string, BigInteger>(StringComparer.Ordinal)
        {
            ["csr_addr"] = address, ["write_enable"] = write ? 1 : 0,
            ["write_value"] = writeValue, ["state_mask"] = stateMask,
            ["state_priority"] = statePriority
        };
        var actualWrite = Evaluate(writeModule, new Dictionary<string, BigInteger>(inputs, StringComparer.Ordinal));
        var actualRead = Evaluate(readModule, new Dictionary<string, BigInteger>(inputs, StringComparer.Ordinal));
        byte expectedMask = write && address == 0x900 ? (byte)((byte)writeValue & 0x1F) : stateMask;
        ushort expectedPriority = statePriority;
        if (write && address == 0x901)
        {
            for (int i = 0; i < 5; i++)
            {
                byte priority = (byte)((writeValue >> (i * 3)) & 0x7);
                int shift = i * 3;
                expectedPriority = (ushort)(expectedPriority & (ushort)~(0x7 << shift));
                expectedPriority = (ushort)(expectedPriority | (ushort)(priority << shift));
            }
        }
        ulong expectedRead;
        if (address == 0x900) expectedRead = stateMask;
        else
        {
            expectedRead = 0;
            for (int i = 0; i < 5; i++)
                expectedRead |= (ulong)((statePriority >> (i * 3)) & 0x7) << (i * 3);
        }
        if (actualWrite["next_mask"] != expectedMask || actualWrite["next_priority"] != expectedPriority ||
            actualWrite["write_address_valid"] != BigInteger.One ||
            actualRead["read_address_valid"] != BigInteger.One || actualRead["read_data"] != expectedRead)
            throw new InvalidOperationException($"Oracle mismatch at vector {count}: addr={address:X3}, we={write}, value={writeValue:X16}, mask={stateMask:X2}, priority={statePriority:X4}.");
        vectors.WriteLine(string.Join(' ', address.ToString("X3", CultureInfo.InvariantCulture),
            (write ? "1" : "0"), writeValue.ToString("X16", CultureInfo.InvariantCulture),
            stateMask.ToString("X2", CultureInfo.InvariantCulture), statePriority.ToString("X4", CultureInfo.InvariantCulture),
            expectedMask.ToString("X2", CultureInfo.InvariantCulture), expectedPriority.ToString("X4", CultureInfo.InvariantCulture),
            "1", expectedRead.ToString("X16", CultureInfo.InvariantCulture)));
        count++;
    }

    // Exhaust all byte values for mask state and writes, with the priority CSR
    // also exercised at each mask point to cover route isolation and bit 15.
    for (int currentMask = 0; currentMask < 256; currentMask++)
    for (int writtenByte = 0; writtenByte < 256; writtenByte++)
    {
        ushort pri = (ushort)(((currentMask & 1) << 15) | ((currentMask * 257 + writtenByte) & 0x7FFF));
        Check(0x900, true, (ulong)writtenByte, (byte)currentMask, pri);
        Check(0x900, false, 0xFFFFFFFFFFFFFFFFUL, (byte)currentMask, pri);
        Check(0x901, true, (ulong)writtenByte, (byte)currentMask, pri);
        Check(0x901, false, 0xFFFFFFFFFFFFFFFFUL, (byte)currentMask, pri);
    }

    // Boundary and deterministic random full-width values stress upper ignored
    // bits, every packed 3-bit field, and preservation of reserved bit 15.
    ulong[] boundaries = [0, 1, 7, 8, 0x7FFF, 0x8000, 0xFFFF, 0x1FFFF, 0xFFFFFFFFFFFFFFFFUL,
        0x8000000000000000UL, 0x123456789ABCDEF0UL, 0xFEDCBA9876543210UL];
    foreach (ulong value in boundaries)
    foreach (ushort state in new ushort[] { 0, 0x8000, 0x7FFF, 0xFFFF, 0xA5A5, 0x5A5A })
    foreach (byte mask in new byte[] { 0, 1, 0x1F, 0xE0, 0xFF })
    foreach (uint address in new uint[] { 0x900, 0x901 })
    foreach (bool write in new[] { false, true }) Check(address, write, value, mask, state);

    var random = new Random(0x56504353);
    byte[] buffer = new byte[18];
    for (int i = 0; i < 100_000; i++)
    {
        random.NextBytes(buffer);
        ulong value = BitConverter.ToUInt64(buffer, 0);
        byte stateMask = buffer[8];
        ushort statePriority = BitConverter.ToUInt16(buffer, 9);
        bool write = (buffer[11] & 1) != 0;
        uint address = (buffer[12] & 1) == 0 ? 0x900u : 0x901u;
        Check(address, write, value, stateMask, statePriority);
    }
    return (count, path);
}

static long RunCounterOracleVectors(HwModule module, string path)
{
    string[] names = ["overflow_count", "underflow_count", "div_by_zero_count", "invalid_op_count", "inexact_count"];
    if (module.Instances.Count != 1 || module.Origin?.Member != "ApplyRetiredCsrEffect" ||
        names.Any(n => !module.Ports.Any(p => p.Name == "current_" + n && p.Direction == HwPortDirection.Input) ||
                       !module.Ports.Any(p => p.Name == "next_" + n && p.Direction == HwPortDirection.Output)))
        throw new InvalidDataException("Retire counter transition interface/provenance is incomplete.");
    long count = 0;
    using var vectors = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    void Check(bool effectPresent, bool clearFlag, uint[] current)
    {
        bool applyClear = effectPresent && clearFlag;
        uint[] expected = applyClear ? new uint[5] : current.ToArray();
        vectors.WriteLine(string.Join(' ', new[] { effectPresent ? "1" : "0", clearFlag ? "1" : "0" }
            .Concat(current.Select(v => v.ToString("X8", CultureInfo.InvariantCulture)))
            .Concat(expected.Select(v => v.ToString("X8", CultureInfo.InvariantCulture)))));
        count++;
    }
    uint[] boundaries = [0, 1, 2, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF, 0x12345678, 0xDEADBEEF];
    foreach (uint value in boundaries)
    foreach (bool present in new[] { false, true })
    foreach (bool clear in new[] { false, true })
        Check(present, clear, names.Select((_, i) => unchecked(value + (uint)(i * 0x01020304))).ToArray());
    var random = new Random(0x52544343);
    byte[] bytes = new byte[22];
    for (int i = 0; i < 20_000; i++)
    {
        random.NextBytes(bytes);
        uint[] state = new uint[5];
        for (int n = 0; n < state.Length; n++) state[n] = BitConverter.ToUInt32(bytes, 2 + n * 4);
        Check((bytes[0] & 1) != 0, (bytes[1] & 1) != 0, state);
    }
    return count;
}

static long RunCsrClearRetirePathVectors(HwModule module, string path)
{
    string[] names = ["overflow_count", "underflow_count", "div_by_zero_count", "invalid_op_count", "inexact_count"];
    if (module.Id != "processor_csr_clear_retire_event_projection" || module.Instances.Count != 3 ||
        module.Origin?.Member != "ApplyCapturedRetireWindowBatch")
        throw new InvalidDataException("CSR_CLEAR decode-to-retire top is missing its three source-bound hierarchical stages.");
    long count = 0;
    using var vectors = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    for (int opcode = 0; opcode <= ushort.MaxValue; opcode++)
    for (int instructionValid = 0; instructionValid <= 1; instructionValid++)
    for (int retireBatchApply = 0; retireBatchApply <= 1; retireBatchApply++)
    {
        bool clear = instructionValid != 0 && opcode == 149 && retireBatchApply != 0;
        uint[] current = names.Select((_, i) => unchecked((uint)(opcode * 0x9E3779B9u + (uint)(i * 0x1020304) + (uint)(instructionValid << 5) + (uint)retireBatchApply))).ToArray();
        uint[] expected = clear ? new uint[5] : current;
        vectors.WriteLine(string.Join(' ', opcode.ToString("X4", CultureInfo.InvariantCulture),
            instructionValid.ToString(CultureInfo.InvariantCulture), retireBatchApply.ToString(CultureInfo.InvariantCulture),
            string.Join(' ', current.Select(v => v.ToString("X8", CultureInfo.InvariantCulture))),
            string.Join(' ', expected.Select(v => v.ToString("X8", CultureInfo.InvariantCulture)))));
        count++;
    }
    return count;
}

(string vectors, string moduleSv) = (Path.Combine(output, "vector-pod-csr-vectors.txt"), Path.Combine(output, "vector-pod-csr.sv"));
(long vectorCount, _) = RunOracleVectors(vectors);
string counterVectors = Path.Combine(output, "vector-exception-counter-vectors.txt");
long counterVectorCount = RunCounterOracleVectors(candidate.Design.Modules.Single(m => m.Id == "retired_csr_exception_counter_transition"), counterVectors);
string retirePathVectors = Path.Combine(output, "csr-clear-retire-path-vectors.txt");
long retirePathVectorCount = RunCsrClearRetirePathVectors(candidate.Design.Modules.Single(m => m.Id == "processor_csr_clear_retire_event_projection"), retirePathVectors);
WriteNew(Path.Combine(output, "vector-pod-csr.hwir.json"), new CanonicalHwirSerializer().Serialize(candidate));
WriteNew(Path.Combine(output, "vector-pod-csr.mlir"), Encoding.UTF8.GetBytes(mlir.Text));
var receipt = new
{
    schema = "circuitweave.vector-pod-csr-candidate-run/v1",
    status = "StructuralOnly",
    activeBaseCommit = BridgeHwirVectorPodCsr.ActiveBaseCommit,
    goldenCommit = BridgeHwirVectorPodCsr.GoldenCommit,
    sourceSha256 = new
    {
        vectorConfig = BridgeHwirVectorPodCsr.VectorConfigSha256,
        microOpControl = BridgeHwirVectorPodCsr.MicroOpControlSha256,
        architecturalState = BridgeHwirVectorPodCsr.ArchitecturalStateSha256,
        csrAddresses = BridgeHwirVectorPodCsr.CsrAddressesSha256,
        goldenVectorConfig = BridgeHwirVectorPodCsr.GoldenVectorConfigSha256,
        goldenMicroOpControl = BridgeHwirVectorPodCsr.GoldenMicroOpControlSha256,
        goldenArchitecturalState = BridgeHwirVectorPodCsr.GoldenArchitecturalStateSha256,
        activeRetire = BridgeHwirVectorRetireCounterClear.ActiveRetireSha256,
        goldenRetire = BridgeHwirVectorRetireCounterClear.GoldenRetireSha256,
        activeCsrClearDecode = Convert.ToHexString(SHA256.HashData(activeDecodeBytes)).ToLowerInvariant(),
        goldenCsrClearDecode = Convert.ToHexString(SHA256.HashData(goldenDecodeBytes)).ToLowerInvariant(),
        activeGeneratedOpcode = Convert.ToHexString(SHA256.HashData(activeOpcodeBytes)).ToLowerInvariant(),
        goldenGeneratedOpcode = Convert.ToHexString(SHA256.HashData(goldenOpcodeBytes)).ToLowerInvariant(),
        activeInstructionClassifier = Convert.ToHexString(SHA256.HashData(activeClassifierBytes)).ToLowerInvariant(),
        goldenInstructionClassifier = Convert.ToHexString(SHA256.HashData(goldenClassifierBytes)).ToLowerInvariant(),
        goldenPipelineControl = Convert.ToHexString(SHA256.HashData(goldenPipelineControlBytes)).ToLowerInvariant(),
        goldenPipelineRetire = Convert.ToHexString(SHA256.HashData(goldenPipelineRetireBytes)).ToLowerInvariant(),
        goldenPipelineState = Convert.ToHexString(SHA256.HashData(goldenPipelineStateBytes)).ToLowerInvariant()
    },
    goldenMethodBodyCheck = "active and pinned Golden read-routing/read-pack/SetMask/SetPriority/GetPriority bodies are token-identical; writer routes are separately pinned and shape-checked because ownership APIs differ",
    csrClearPathSourceContractCheck = "active and pinned Golden CSR_CLEAR classifier arm, published decode case, CsrClearMicroOp/CreateRetireEffect, materialization wrappers, prevalidate/apply order, and five-counter clear action were checked from exact pinned bytes; active canonical-core write reservation differs from Golden",
    retireEventInputMeaning = "One software call-event boundary: this CSR_CLEAR effect is in the successfully prevalidated captured batch and ApplyCapturedRetireWindowBatch is being applied. It is not a physical clock, handshake, or measured retire cycle.",
    modules = candidate.Design.Modules.Select(m => new { id = m.Id, originPath = m.Origin?.Path, originType = m.Origin?.Type, originMember = m.Origin?.Member, originSha256 = m.Origin?.IdentityDigest }),
    designTopPointer = candidate.Design.TopModule,
    designTopMeaning = "CSR_CLEAR call-event vertical projection: source-bound opcode decode -> CSR_CLEAR micro-op retire effect -> captured retire-batch application event -> five counter next-state outputs. It excludes other instructions and physical clock/reset/stall/replay timing; this is not a composed CPU top.",
    addressValidity = "address_valid only recognizes this projection domain, CSR addresses 0x900 and 0x901; it is not a handshake. Other addresses are out of scope because unsupported software CSR routes throw.",
    staleSourceRejected,
    hwirSha256 = candidate.CanonicalDigest,
    mlirSha256 = mlir.ContentDigest,
    vectorCount,
    counterVectorCount,
    csrClearRetirePathVectorCount = retirePathVectorCount,
    expectedCounterVectorFileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(counterVectors))).ToLowerInvariant(),
    expectedCsrClearRetirePathVectorFileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(retirePathVectors))).ToLowerInvariant(),
    expectedVectorFileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vectors))).ToLowerInvariant(),
    statefulImplementation = false,
    physicalClockResetStallHandshake = "unmodeled",
    goldenEquivalence = false,
    note = "CSR_CLEAR decode-to-retire call-event projection plus combinational Vector CSR next-state; vectors do not establish CPU state timing or active/Golden sequential equivalence."
};
WriteNew(Path.Combine(output, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Vector CSR + CSR_CLEAR retire-event HWIR validated and hierarchically lowered; {candidate.Design.Modules.Count} modules / {candidate.Design.Modules.Sum(m => m.Nodes.Count)} nodes, {vectorCount} CSR vectors, {counterVectorCount} counter vectors, {retirePathVectorCount} decode-to-retire vectors; StructuralOnly.");
Console.WriteLine($"HWIR={candidate.CanonicalDigest} MLIR={mlir.ContentDigest} CSRVectors={receipt.expectedVectorFileSha256} CounterVectors={receipt.expectedCounterVectorFileSha256}");

static void WriteNew(string path, byte[] bytes)
{
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    file.Write(bytes);
}

static async Task PublishExistingCandidate(string hwirPath, string evidenceDirectory, string workspace)
{
    string fullWorkspace = Path.GetFullPath(workspace);
    if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(fullWorkspace))
        throw new ArgumentException("Publication workspace must be an existing absolute directory.");
    string[] files = ["vector-pod-csr.mlir", "vector-pod-csr.sv", "vector-pod-csr-vectors.txt",
        "vector-exception-counter-vectors.txt", "VectorPodCsrVectors.sv", "VectorRetireCounterClearVectors.sv",
        "csr-clear-retire-path-vectors.txt", "CsrClearRetirePathVectors.sv",
        "simulation.log", "counter-simulation.log", "csr-clear-retire-path-simulation.log", "golden-csr-clear-pipeline-source-manifest.json", "candidate-receipt.json",
        "simulation-receipt.json"];
    byte[] hwir = File.ReadAllBytes(hwirPath);
    HwDesign design = new CanonicalHwirSerializer().Deserialize(hwir);
    HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
    if (!validation.Accepted || validation.Validated is null)
        throw new InvalidDataException("Stored Vector CSR HWIR did not pass the full fail-closed validator.");
    HwValidatedDesign validated = validation.Validated;
    string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    List<StudioSupplementArtifact> supplements = [];
    string runtimeRoot = Environment.GetEnvironmentVariable("CW_CSR_CLEAR_RUNTIME_ROOT") ?? string.Empty;
    bool runtimeVerified = !string.IsNullOrWhiteSpace(runtimeRoot);
    byte[]? runtimeComparisonBytes = null;
    byte[]? activeRuntimeTraceBytes = null;
    byte[]? goldenRuntimeTraceBytes = null;
    byte[]? activeRuntimeLogBytes = null;
    byte[]? goldenRuntimeLogBytes = null;
    byte[]? runtimeToRtlReceiptBytes = null;
    if (runtimeVerified)
    {
        runtimeComparisonBytes = File.ReadAllBytes(Path.Combine(runtimeRoot, "comparison.json"));
        activeRuntimeTraceBytes = File.ReadAllBytes(Path.Combine(runtimeRoot, "active-trace.json"));
        goldenRuntimeTraceBytes = File.ReadAllBytes(Path.Combine(runtimeRoot, "golden-trace.json"));
        activeRuntimeLogBytes = File.ReadAllBytes(Path.Combine(runtimeRoot, "active-test.log"));
        goldenRuntimeLogBytes = File.ReadAllBytes(Path.Combine(runtimeRoot, "golden-test.log"));
        runtimeToRtlReceiptBytes = File.ReadAllBytes(Path.Combine(fullWorkspace, "csr-clear-runtime-to-rtl-receipt.json"));
        using JsonDocument comparisonDoc = JsonDocument.Parse(runtimeComparisonBytes);
        JsonElement comparison = comparisonDoc.RootElement;
        using JsonDocument runtimeReceiptDoc = JsonDocument.Parse(runtimeToRtlReceiptBytes);
        JsonElement runtimeReceipt = runtimeReceiptDoc.RootElement;
        if (comparison.GetProperty("schema").GetString() != "circuitweave.csr-clear-runtime-differential/v1" ||
            comparison.GetProperty("activeModelCommit").GetString() != BridgeHwirVectorPodCsr.ActiveBaseCommit ||
            comparison.GetProperty("activeModelGitHead").GetString() != BridgeHwirVectorPodCsr.ActiveBaseCommit ||
            comparison.GetProperty("goldenCommit").GetString() != BridgeHwirVectorPodCsr.GoldenCommit ||
            !comparison.GetProperty("tracesEqual").GetBoolean() || comparison.GetProperty("eventCount").GetInt32() != 3 ||
            comparison.GetProperty("activeTraceSha256").GetString() != comparison.GetProperty("goldenTraceSha256").GetString() ||
            comparison.GetProperty("activeTraceSha256").GetString() != Sha(activeRuntimeTraceBytes) ||
            comparison.GetProperty("goldenTraceSha256").GetString() != Sha(goldenRuntimeTraceBytes) ||
            comparison.GetProperty("activeTraceTestLogSha256").GetString() != Sha(activeRuntimeLogBytes) ||
            comparison.GetProperty("goldenTraceTestLogSha256").GetString() != Sha(goldenRuntimeLogBytes) ||
            runtimeReceipt.GetProperty("status").GetString() != "PASS_ACTIVE_GOLDEN_RUNTIME_TRACE_TO_HWIREQUATION_TO_ICARUS" ||
            runtimeReceipt.GetProperty("runtimeEventCount").GetInt32() != 3 ||
            runtimeReceipt.GetProperty("runtimeTraceSha256").GetString() != Sha(activeRuntimeTraceBytes) ||
            runtimeReceipt.GetProperty("simulatedRuntimeVectors").GetInt32() != 3 ||
            runtimeReceipt.GetProperty("mismatches").GetInt32() != 0 ||
            runtimeReceipt.GetProperty("vectorFileSha256").GetString() != Sha(File.ReadAllBytes(Path.Combine(fullWorkspace, "csr-clear-retire-path-vectors.txt"))) ||
            runtimeReceipt.GetProperty("rtlSha256").GetString() != Sha(File.ReadAllBytes(Path.Combine(fullWorkspace, "vector-pod-csr.sv"))) ||
            runtimeReceipt.GetProperty("testbenchSha256").GetString() != Sha(File.ReadAllBytes(Path.Combine(fullWorkspace, "CsrClearRetirePathVectors.sv"))) ||
            runtimeReceipt.GetProperty("simulationLogSha256").GetString() != Sha(File.ReadAllBytes(Path.Combine(fullWorkspace, "csr-clear-retire-path-simulation.log"))))
            throw new InvalidDataException("Active/Golden CSR_CLEAR runtime/RTL evidence is stale, missing, or inconsistent.");
        files = [.. files, "verification-receipt-v3.json", "csr-clear-runtime-to-rtl-receipt.json"];
        foreach (var (id, file) in new[]
        {
            ("csr-clear-runtime-comparison", "comparison.json"),
            ("csr-clear-active-runtime-trace", "active-trace.json"),
            ("csr-clear-golden-runtime-trace", "golden-trace.json"),
            ("csr-clear-active-runtime-test-log", "active-test.log"),
            ("csr-clear-golden-runtime-test-log", "golden-test.log")
        })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(runtimeRoot, file));
            supplements.Add(new(id, StudioArtifactKind.CandidateRunProvenance, "csr-clear-" + file, bytes, Sha(bytes)));
        }
    }
    bool hasSourceCapture = File.Exists(Path.Combine(evidenceDirectory, "vector-pod-csr-source-inventory.json"));
    byte[] note = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "circuitweave.vector-pod-csr-studio-candidate/v1",
        status = "StructuralOnly",
        scope = "source-bound Vector CSR projections plus CSR_CLEAR opcode decode, micro-op effect, selected retire-batch apply call-event gate, and five-counter next-state projection; no composed CPU top",
        activeModelHead = BridgeHwirVectorPodCsr.ActiveBaseCommit,
        goldenCommit = BridgeHwirVectorPodCsr.GoldenCommit,
        sourceFiles = new[] { "InstructionRegistry.Helpers.Csr.cs", "InstructionClassifier.cs", "GeneratedIsaOpcodeValues.g.cs", "MicroOp.Control.cs", "CPU_Core.PipelineExecution.Retire.cs", "CPU_Core.State.cs", "CPU_Core.VectorConfig.cs", "CsrAddresses.cs" },
        modules = validated.Design.Modules.Select(m => new { m.Id, m.Origin }),
        independentSimulation = runtimeVerified
            ? "363584 Vector CSR vectors, 20036 counter transition vectors, and 262147 CSR_CLEAR decode-to-retire vectors passed in Icarus, including three active/Golden runtime-derived samples; call-event projection only"
            : "363584 Vector CSR vectors, 20036 counter transition vectors, and 262144 CSR_CLEAR decode-to-retire call-event vectors passed in Icarus; finite combinational check only",
        sourceInventoryScope = !hasSourceCapture
            ? "not supplied"
            : "partial five-type, sixteen-member Roslyn projection with exact captured bytes for five source files; not full Processor inventory",
        notEstablished = new[] { "complete Processor semantic inventory", "physical clock/reset/stall/replay timing", "canonical-core reservation and active/Golden sequential equivalence", "selected-prefix retire/trap/replay cycle traces", "CPU top" }
    }, new JsonSerializerOptions { WriteIndented = true });
    supplements.Add(new("vector-pod-csr-candidate", StudioArtifactKind.CandidateRunProvenance,
        "vector-pod-csr-candidate.json", note, Sha(note)));
    byte[] hwirBytes = hwir;
    supplements.Add(new("vector-pod-csr-run-hwir", StudioArtifactKind.CandidateRunProvenance,
        "vector-pod-csr.hwir.json", hwirBytes, Sha(hwirBytes)));
    byte[]? inventoryBytes = null;
    string inventoryPath = Path.Combine(evidenceDirectory, "vector-pod-csr-source-inventory.json");
    string capturedPath = Path.Combine(evidenceDirectory, "vector-pod-csr-captured-sources.json");
    if (File.Exists(inventoryPath) != File.Exists(capturedPath))
        throw new InvalidDataException("The partial source inventory and captured-source bundle must be supplied together.");
    if (File.Exists(inventoryPath))
    {
        inventoryBytes = File.ReadAllBytes(inventoryPath);
        byte[] capturedBytes = File.ReadAllBytes(capturedPath);
        using JsonDocument inventory = JsonDocument.Parse(inventoryBytes);
        using JsonDocument captured = JsonDocument.Parse(capturedBytes);
        JsonElement inventoryRoot = inventory.RootElement;
        JsonElement capturedRoot = captured.RootElement;
        if (inventoryRoot.GetProperty("schema").GetString() != "circuitweave.source-coverage-index/v1" ||
            inventoryRoot.GetProperty("disposition").GetString() != "incomplete-unqualified" ||
            capturedRoot.GetProperty("schema").GetString() != "circuitweave.captured-sources/v1" ||
            capturedRoot.GetProperty("inventorySha256").GetString() != Sha(inventoryBytes))
            throw new InvalidDataException("Vector CSR partial source inventory/capture identity mismatch.");
        Dictionary<string, JsonElement> capturedFiles = capturedRoot.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("path").GetString()!, StringComparer.OrdinalIgnoreCase);
        JsonElement identities = inventoryRoot.GetProperty("capturedSourceFiles");
        if (identities.GetArrayLength() != capturedFiles.Count)
            throw new InvalidDataException("Vector CSR captured file count differs from its inventory.");
        foreach (JsonElement identity in identities.EnumerateArray())
        {
            string path = identity.GetProperty("path").GetString()!;
            if (!capturedFiles.TryGetValue(path, out JsonElement capturedFile))
                throw new InvalidDataException("Vector CSR captured file is not in the source bundle: " + path);
            byte[] sourceBytes = capturedFile.GetProperty("bytes").GetBytesFromBase64();
            if (sourceBytes.LongLength != identity.GetProperty("length").GetInt64() ||
                Sha(sourceBytes) != identity.GetProperty("sha256").GetString() ||
                !File.ReadAllBytes(path).SequenceEqual(sourceBytes))
                throw new InvalidDataException("Vector CSR captured source is stale or differs from its inventory: " + path);
        }
        supplements.Add(new("source-inventory", StudioArtifactKind.SourceInventory,
            "source-inventory.json", inventoryBytes, Sha(inventoryBytes)));
        supplements.Add(new("captured-sources", StudioArtifactKind.CapturedSources,
            "captured-sources.json", capturedBytes, Sha(capturedBytes)));
    }
    foreach (string file in files)
    {
        string path = Path.Combine(evidenceDirectory, file);
        byte[] bytes = File.ReadAllBytes(path);
        StudioArtifactKind kind = file switch
        {
            "vector-pod-csr.mlir" => StudioArtifactKind.Mlir,
            "vector-pod-csr.sv" => StudioArtifactKind.SystemVerilog,
            _ => StudioArtifactKind.CandidateRunProvenance
        };
        string id = "vector-pod-csr-" + file.Replace('.', '-');
        supplements.Add(new(id, kind, file, bytes, Sha(bytes)));
    }
    CompilationStageRecord[] stages = Enum.GetValues<CompilationStage>().Select(stage =>
        new CompilationStageRecord(stage,
            stage is CompilationStage.HardwareIrValidation or CompilationStage.RtlEmission
                ? CompilationStageStatus.Completed : CompilationStageStatus.NotRun,
            stage switch
            {
                CompilationStage.HardwareIrValidation => "Seven-module Vector CSR plus CSR_CLEAR decode-to-retire event projection passed the fail-closed HWIR validator.",
                CompilationStage.RtlEmission => runtimeVerified
                    ? "CIRCT-exported candidate passed 363584 CSR, 20036 counter transition, 262144 exhaustive decode-to-retire, and three active/Golden runtime-derived call-event vectors in Icarus. This is bounded software event projection, not sequential equivalence."
                    : "CIRCT-exported candidate passed 363584 CSR, 20036 counter transition, and 262144 decode-to-retire call-event finite Icarus vectors; this is not Golden equivalence.",
                _ => "Not run for this bounded combinational candidate."
            })).ToArray();
    var request = new StudioCompilerPublicationRequest("hybridcpu-vector-pod-csr",
        "HybridCPU Vector CSR combinational candidate", "vector-pod-csr-" + validated.CanonicalDigest[..12], validated,
        new HwTargetDescription("combinational", "generic", "circt", "HWIR comb candidate"), stages,
        Supplements: supplements, SourceDigest: inventoryBytes is null ? validated.CanonicalDigest : Sha(inventoryBytes));
    StudioPublicationReceipt receipt = await new StudioSnapshotPublisher().PublishAsync(request, fullWorkspace);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = receipt.Status.ToString(), receipt.StableManifestPath, receipt.SnapshotDirectory,
        receipt.SnapshotDigest, receipt.ManifestSha256,
        modules = validated.Design.Modules.Count,
        sourceMap = "publisher-generated partial map from source-bound HWIR origins",
        capturedSourceNavigation = !hasSourceCapture
            ? "not supplied: requires digest-qualified source inventory"
            : "partial five-type/sixteen-member source inventory and exact captured text supplied for the source-bound CSR_CLEAR path"
    }, new JsonSerializerOptions { WriteIndented = true }));
}

