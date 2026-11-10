using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record StreamVectorSpecSuiteReport(
    string SuiteId,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    ulong Iterations,
    IReadOnlyList<string> ArchitectureKeys,
    IReadOnlyList<StreamVectorSpecScenarioReport> Scenarios,
    StreamVectorSpecAggregate Aggregate)
{
    public bool Succeeded => Scenarios.All(static scenario => scenario.Passed);
}

internal sealed record StreamVectorSpecAggregate(
    int ScenarioCount,
    int PassedScenarioCount,
    ulong DynamicInstructionCount,
    ulong VectorElementsProcessed,
    ulong ModeledBytesTouched,
    double ElapsedMilliseconds,
    ulong ResultChecksum);

internal sealed record StreamVectorSpecScenarioReport(
    string Id,
    string Algorithm,
    string ExecutionSurface,
    string WorkloadShape,
    ulong Iterations,
    int StaticInstructionCount,
    ulong DynamicInstructionCount,
    ulong VectorElementsPerIteration,
    ulong VectorElementsProcessed,
    ulong ModeledBytesPerIteration,
    ulong ModeledBytesTouched,
    double ElapsedMilliseconds,
    bool Passed,
    string FailureMessage,
    double MaxAbsoluteError,
    ulong ResultChecksum,
    IReadOnlyList<string> Opcodes,
    StreamVectorSpecTelemetry Telemetry);

internal sealed record StreamVectorSpecTelemetry(
    long TotalBursts,
    long TotalBytesTransferred,
    long ForegroundWarmAttempts,
    long ForegroundWarmSuccesses,
    long ForegroundWarmReuseHits,
    long ForegroundBypassHits,
    long AssistWarmAttempts,
    long AssistWarmSuccesses,
    long AssistWarmReuseHits,
    long AssistBypassHits,
    long StreamWarmTranslationRejects,
    long StreamWarmBackendRejects,
    ulong DmaBytesRead,
    ulong DmaBytesStaged,
    int DmaReadBurstCount,
    ulong DmaModeledLatencyCycles,
    bool DmaUsedLane6Backend,
    int DmaDirectDestinationWrites,
    ulong DmaElementOperations);

internal sealed class StreamVectorSpecSuite
{
    private const int FloatBytes = sizeof(float);
    private const int WordBytes = sizeof(uint);

    private const int MatrixN = 4;
    private const int FirInputCount = 12;
    private const int FirTapCount = 8;
    private const int FirOutputCount = 4;
    private const int CompressionElementCount = 16;
    private const int CryptoElementCount = 16;
    private const int StencilRows = 6;
    private const int StencilCols = 6;
    private const int StencilInterior = 4;

    private const ulong SgemmAAddress = 0x0020_0000UL;
    private const ulong SgemmBAddress = 0x0021_0000UL;
    private const ulong SgemmCAddress = 0x0022_0000UL;
    private const ulong SgemmTempAddress = 0x0023_0000UL;

    private const ulong FirInputAddress = 0x0030_0000UL;
    private const ulong FirTapAddress = 0x0031_0000UL;
    private const ulong FirWindowAddress = 0x0032_0000UL;

    private const ulong CompressValuesAddress = 0x0040_0000UL;
    private const ulong CompressThresholdsAddress = 0x0041_0000UL;
    private const byte CompressPredicateRegister = 5;

    private const ulong CryptoStateAddress = 0x0050_0000UL;
    private const ulong CryptoKeyAddress = 0x0051_0000UL;
    private const ulong CryptoShiftLeftAddress = 0x0052_0000UL;
    private const ulong CryptoAddAddress = 0x0053_0000UL;
    private const ulong CryptoShiftRightAddress = 0x0054_0000UL;
    private const ulong CryptoSaltAddress = 0x0055_0000UL;

    private const ulong StencilGridAddress = 0x0060_0000UL;
    private const ulong StencilOutputAddress = 0x0061_0000UL;

    private const ulong DmaSrcAAddress = 0x0070_0000UL;
    private const ulong DmaSrcBAddress = 0x0071_0000UL;
    private const ulong DmaSrcCAddress = 0x0072_0000UL;
    private const ulong DmaFmaOutputAddress = 0x0073_0000UL;
    private const ulong DmaReduceOutputAddress = 0x0074_0000UL;
    private const ulong DmaDescriptorAddress = 0x0075_0000UL;
    private const ulong DmaIdentityHash = 0xF007_0000_0000_0001UL;

    private static readonly float[] SgemmA =
    [
        1.0f, 2.0f, 3.0f, 4.0f,
        0.5f, -1.0f, 2.5f, 3.0f,
        4.0f, 1.5f, -2.0f, 0.25f,
        2.0f, 0.0f, 1.0f, -3.0f
    ];

    private static readonly float[] SgemmB =
    [
        2.0f, 0.0f, 1.0f, -1.0f,
        -2.0f, 1.0f, 0.5f, 3.0f,
        0.25f, 4.0f, -1.0f, 2.0f,
        1.5f, -0.5f, 2.0f, 0.0f
    ];

    private static readonly float[] FirInput =
    [
        0.20f, -0.50f, 1.25f, 0.75f, -1.00f, 0.50f,
        1.50f, -0.25f, 0.40f, 0.80f, -0.90f, 1.10f
    ];

    private static readonly float[] FirTaps =
    [
        0.125f, -0.250f, 0.500f, 0.750f,
        -0.375f, 0.250f, 0.0625f, -0.125f
    ];

    private static readonly uint[] CompressValues =
    [
        11U, 2U, 43U, 8U, 19U, 27U, 3U, 41U,
        5U, 60U, 17U, 18U, 90U, 1U, 7U, 33U
    ];

    private static readonly uint[] CompressThresholds =
    [
        10U, 10U, 40U, 10U, 20U, 25U, 5U, 40U,
        4U, 55U, 30U, 17U, 80U, 2U, 7U, 32U
    ];

    private static readonly uint[] CryptoState =
    [
        0x0123_4567U, 0x89AB_CDEFU, 0x1020_3040U, 0x5566_7788U,
        0xDEAD_BEEFU, 0xA5A5_5A5AU, 0x0F0F_F0F0U, 0x1357_9BDFU,
        0x2468_ACE0U, 0xCAFE_BABEU, 0xFACE_FEEDU, 0x0101_0101U,
        0xFEED_C0DEU, 0x8080_0001U, 0x7FFF_FFFFU, 0x3333_CCCCU
    ];

    private static readonly uint[] CryptoKey =
    [
        0xA5A5_A5A5U, 0x0102_0304U, 0xFFFF_0000U, 0x0BAD_F00DU,
        0x1111_2222U, 0x7654_3210U, 0xAA55_AA55U, 0x5555_AAAAU,
        0xDEAD_0001U, 0x0000_FFFFU, 0x1234_5678U, 0x8765_4321U,
        0x0F0F_0F0FU, 0xF0F0_F0F0U, 0x3141_5926U, 0x2718_2818U
    ];

    private static readonly uint[] CryptoShiftLeft =
    [
        1U, 3U, 5U, 7U, 9U, 11U, 13U, 15U,
        17U, 19U, 21U, 23U, 25U, 27U, 29U, 31U
    ];

    private static readonly uint[] CryptoAdd =
    [
        0x0101_0101U, 0x0202_0202U, 0x0303_0303U, 0x0404_0404U,
        0x0505_0505U, 0x0606_0606U, 0x0707_0707U, 0x0808_0808U,
        0x0909_0909U, 0x0A0A_0A0AU, 0x0B0B_0B0BU, 0x0C0C_0C0CU,
        0x0D0D_0D0DU, 0x0E0E_0E0EU, 0x0F0F_0F0FU, 0x1010_1010U
    ];

    private static readonly uint[] CryptoShiftRight =
    [
        1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U,
        9U, 10U, 11U, 12U, 13U, 14U, 15U, 16U
    ];

    private static readonly uint[] CryptoSalt =
    [
        0x0000_00FFU, 0x0000_FF00U, 0x00FF_0000U, 0xFF00_0000U,
        0x0F0F_0000U, 0x0000_0F0FU, 0x00F0_00F0U, 0xF000_000FU,
        0x3333_0000U, 0x0000_3333U, 0x5555_0000U, 0x0000_5555U,
        0xAAAA_0000U, 0x0000_AAAAU, 0xCCCC_0000U, 0x0000_CCCCU
    ];

    private static readonly uint[] DmaSourceA = [1U, 2U, 3U, 4U];
    private static readonly uint[] DmaSourceB = [10U, 20U, 30U, 40U];
    private static readonly uint[] DmaSourceC = [7U, 8U, 9U, 10U];

    public StreamVectorSpecSuiteReport Execute(ulong iterations)
    {
        if (iterations == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "Iterations must be positive.");
        }

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        List<StreamVectorSpecScenarioReport> scenarios = new List<StreamVectorSpecScenarioReport>
        {
            RunSgemm(iterations),
            RunFir(iterations),
            RunPredicateCompression(iterations),
            RunCryptoBitMix(iterations),
            RunStencil(iterations),
            RunDmaDescriptorContract(iterations)
        };

        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        return new StreamVectorSpecSuiteReport(
            SuiteId: "stream-vector-spec-suite",
            StartedUtc: startedUtc,
            FinishedUtc: finishedUtc,
            Iterations: iterations,
            ArchitectureKeys:
            [
                "StreamEngine raw/direct VLOAD/VSTORE and descriptorless FMA remain fail-closed; executable vectors use materialized mainline carriers.",
                "VectorALU coverage: binary arithmetic, dot product, comparisons, predicative movement, shifts, bit ops, population count.",
                "DmaStreamCompute coverage: descriptor v1, lane6 typed-slot evidence, runtime staging token, guard-authorized commit, no direct destination writes.",
                "2D/indexed positive workloads are intentionally decomposed to 1D row sweeps because unsupported raw helper surfaces must reject instead of hidden success."
            ],
            Scenarios: scenarios,
            Aggregate: BuildAggregate(scenarios));
    }

    private static StreamVectorSpecScenarioReport RunSgemm(ulong iterations)
    {
        VLIW_Instruction[] program = BuildSgemmProgram();
        return RunVectorScenario(
            id: "sgemm-4x4-stream-rows",
            algorithm: "Dense SGEMM micro-kernel C=A*B",
            executionSurface: "Materialized vector transfer + VectorALU binary carriers",
            workloadShape: "4x4 FP32 matrix multiply; B rows streamed through VLOAD, broadcast A scalars through VMUL, accumulated with VADD.",
            iterations,
            program,
            seedMemory: SeedSgemmMemory,
            validate: _ => ValidateSgemm(),
            vectorElementsPerIteration: MatrixN * MatrixN * MatrixN * 3UL,
            modeledBytesPerIteration: MatrixN * MatrixN * (16UL + 48UL + 48UL));
    }

    private static StreamVectorSpecScenarioReport RunFir(ulong iterations)
    {
        VLIW_Instruction[] program = BuildFirProgram();
        return RunVectorScenario(
            id: "fir-vdotf-windowed-dsp",
            algorithm: "DSP FIR convolution",
            executionSurface: "Materialized VectorDotProductMicroOp",
            workloadShape: "4 FP32 output samples, each as an 8-tap VDOTF sliding window.",
            iterations,
            program,
            seedMemory: SeedFirMemory,
            validate: _ => ValidateFir(),
            vectorElementsPerIteration: FirOutputCount * FirTapCount,
            modeledBytesPerIteration: FirOutputCount * ((FirTapCount * 2UL * FloatBytes) + FloatBytes));
    }

    private static StreamVectorSpecScenarioReport RunPredicateCompression(ulong iterations)
    {
        VLIW_Instruction[] program = BuildPredicateCompressionProgram();
        return RunVectorScenario(
            id: "predicate-compress-filter",
            algorithm: "Columnar analytics filter/pack",
            executionSurface: "Materialized comparison + predicative movement carriers",
            workloadShape: "16 UINT32 values filtered by VCMPGT into predicate register p5, then VCOMPRESS packs active lanes in-place.",
            iterations,
            program,
            seedMemory: SeedPredicateCompressionMemory,
            validate: ValidatePredicateCompression,
            vectorElementsPerIteration: CompressionElementCount * 2UL,
            modeledBytesPerIteration: (CompressionElementCount * 2UL * WordBytes) + (CompressionElementCount * 2UL * WordBytes),
            prepareCore: static core => core.SetPredicateRegister(CompressPredicateRegister, ulong.MaxValue));
    }

    private static StreamVectorSpecScenarioReport RunCryptoBitMix(ulong iterations)
    {
        VLIW_Instruction[] program = BuildCryptoProgram();
        return RunVectorScenario(
            id: "crypto-bitmix-popcount",
            algorithm: "Crypto/hash bit-mixing round",
            executionSurface: "Materialized VectorALU logical, shift, add, and unary bit-manip carriers",
            workloadShape: "16 UINT32 lanes: XOR key, shift-left, add constant, shift-right, OR salt, VPOPCNT.",
            iterations,
            program,
            seedMemory: SeedCryptoMemory,
            validate: _ => ValidateCrypto(),
            vectorElementsPerIteration: CryptoElementCount * 6UL,
            modeledBytesPerIteration: (CryptoElementCount * 5UL * 3UL * WordBytes) + (CryptoElementCount * 2UL * WordBytes));
    }

    private static StreamVectorSpecScenarioReport RunStencil(ulong iterations)
    {
        VLIW_Instruction[] program = BuildStencilProgram();
        return RunVectorScenario(
            id: "hydro-row-stencil-5point",
            algorithm: "Hydrodynamics-like 5-point stencil",
            executionSurface: "Materialized VectorALU row-wise 1D carriers",
            workloadShape: "6x6 UINT32 grid, 4 interior rows x 4 columns. 2D math is expressed as legal 1D row sweeps.",
            iterations,
            program,
            seedMemory: SeedStencilMemory,
            validate: _ => ValidateStencil(),
            vectorElementsPerIteration: StencilInterior * 4UL * StencilInterior,
            modeledBytesPerIteration: StencilInterior * 4UL * StencilInterior * 3UL * WordBytes);
    }

    private static StreamVectorSpecScenarioReport RunDmaDescriptorContract(ulong iterations)
    {
        const int DescriptorSubmissionsPerIteration = 2;
        Stopwatch stopwatch = Stopwatch.StartNew();
        ValidationResult validation = ValidationResult.Success(0, 0);
        ulong dmaBytesRead = 0;
        ulong dmaBytesStaged = 0;
        int dmaReadBursts = 0;
        ulong dmaLatencyCycles = 0;
        ulong dmaElementOps = 0;
        bool usedLane6Backend = true;
        int directWrites = 0;

        BootstrapRuntime();

        try
        {
            for (ulong iteration = 0; iteration < iterations; iteration++)
            {
                SeedDmaMemory();

                DmaStreamComputeTelemetryCounters telemetryCounters = new DmaStreamComputeTelemetryCounters();
                DmaStreamComputeDescriptor fmaDescriptor = CreateDmaDescriptor(
                    operation: DmaStreamComputeOperationKind.Fma,
                    shape: DmaStreamComputeShapeKind.Contiguous1D,
                    readRanges:
                    [
                        new DmaStreamComputeMemoryRange(DmaSrcAAddress, 16),
                        new DmaStreamComputeMemoryRange(DmaSrcBAddress, 16),
                        new DmaStreamComputeMemoryRange(DmaSrcCAddress, 16)
                    ],
                    writeRanges:
                    [
                        new DmaStreamComputeMemoryRange(DmaFmaOutputAddress, 16)
                    ],
                    descriptorOrdinal: 0);

                DmaStreamComputeDescriptor reduceDescriptor = CreateDmaDescriptor(
                    operation: DmaStreamComputeOperationKind.Reduce,
                    shape: DmaStreamComputeShapeKind.FixedReduce,
                    readRanges:
                    [
                        new DmaStreamComputeMemoryRange(DmaSrcAAddress, 16)
                    ],
                    writeRanges:
                    [
                        new DmaStreamComputeMemoryRange(DmaReduceOutputAddress, 4)
                    ],
                    descriptorOrdinal: 1);

                validation = ValidateDmaMicroOpPlacement(fmaDescriptor);
                if (!validation.Passed)
                {
                    break;
                }

                validation = ExecuteDmaDescriptor(fmaDescriptor, telemetryCounters, tokenId: (iteration * 2UL) + 1UL);
                AccumulateLastDmaEvidence(
                    ref dmaReadBursts,
                    ref dmaLatencyCycles,
                    ref usedLane6Backend,
                    ref directWrites);
                if (!validation.Passed)
                {
                    break;
                }

                validation = ExecuteDmaDescriptor(reduceDescriptor, telemetryCounters, tokenId: (iteration * 2UL) + 2UL);
                AccumulateLastDmaEvidence(
                    ref dmaReadBursts,
                    ref dmaLatencyCycles,
                    ref usedLane6Backend,
                    ref directWrites);
                if (!validation.Passed)
                {
                    break;
                }

                validation = ValidateDmaResults(telemetryCounters);
                if (!validation.Passed)
                {
                    break;
                }

                DmaStreamComputeTelemetrySnapshot snapshot = telemetryCounters.Snapshot();
                dmaBytesRead += snapshot.BytesRead;
                dmaBytesStaged += snapshot.BytesStaged;
                dmaElementOps += snapshot.ElementOperations;
            }
        }
        catch (Exception ex)
        {
            validation = ValidationResult.Failure(ex.Message);
        }

        stopwatch.Stop();

        StreamVectorSpecTelemetry telemetry = new StreamVectorSpecTelemetry(
            TotalBursts: 0,
            TotalBytesTransferred: 0,
            ForegroundWarmAttempts: 0,
            ForegroundWarmSuccesses: 0,
            ForegroundWarmReuseHits: 0,
            ForegroundBypassHits: 0,
            AssistWarmAttempts: 0,
            AssistWarmSuccesses: 0,
            AssistWarmReuseHits: 0,
            AssistBypassHits: 0,
            StreamWarmTranslationRejects: 0,
            StreamWarmBackendRejects: 0,
            DmaBytesRead: dmaBytesRead,
            DmaBytesStaged: dmaBytesStaged,
            DmaReadBurstCount: dmaReadBursts,
            DmaModeledLatencyCycles: dmaLatencyCycles,
            DmaUsedLane6Backend: usedLane6Backend,
            DmaDirectDestinationWrites: directWrites,
            DmaElementOperations: dmaElementOps);

        return new StreamVectorSpecScenarioReport(
            Id: "dma-lane6-token-contract",
            Algorithm: "Descriptor-backed memory-memory compute",
            ExecutionSurface: "DmaStreamComputeRuntime staging token + guard-authorized CPU commit",
            WorkloadShape: "UINT32 FMA over 4 lanes plus fixed reduce; direct DmaStreamComputeMicroOp.Execute remains fail-closed.",
            Iterations: iterations,
            StaticInstructionCount: DescriptorSubmissionsPerIteration,
            DynamicInstructionCount: iterations * DescriptorSubmissionsPerIteration,
            VectorElementsPerIteration: 8,
            VectorElementsProcessed: iterations * 8UL,
            ModeledBytesPerIteration: (3UL * 16UL) + 16UL + 16UL + 4UL,
            ModeledBytesTouched: iterations * ((3UL * 16UL) + 16UL + 16UL + 4UL),
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            Passed: validation.Passed,
            FailureMessage: validation.FailureMessage,
            MaxAbsoluteError: validation.MaxAbsoluteError,
            ResultChecksum: validation.ResultChecksum,
            Opcodes: ["DmaStreamCompute.Fma", "DmaStreamCompute.Reduce"],
            Telemetry: telemetry);
    }

    private static (int ReadBursts, ulong ModeledLatencyCycles, bool UsedLane6Backend, int DirectWrites) _lastDmaBackendEvidence;

    private static void AccumulateLastDmaEvidence(
        ref int readBursts,
        ref ulong modeledLatencyCycles,
        ref bool usedLane6Backend,
        ref int directWrites)
    {
        (int bursts, ulong latency, bool lane6, int directDestinationWrites) = _lastDmaBackendEvidence;
        readBursts += bursts;
        modeledLatencyCycles += latency;
        usedLane6Backend &= lane6;
        directWrites += directDestinationWrites;
    }

    private static StreamVectorSpecScenarioReport RunVectorScenario(
        string id,
        string algorithm,
        string executionSurface,
        string workloadShape,
        ulong iterations,
        VLIW_Instruction[] program,
        Action seedMemory,
        Func<Processor.CPU_Core, ValidationResult> validate,
        ulong vectorElementsPerIteration,
        ulong modeledBytesPerIteration,
        Action<Processor.CPU_Core>? prepareCore = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(seedMemory);
        ArgumentNullException.ThrowIfNull(validate);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ValidationResult validation = ValidationResult.Success(0, 0);
        PerformanceReport performance = new();

        BootstrapRuntime();

        try
        {
            for (ulong iteration = 0; iteration < iterations; iteration++)
            {
                seedMemory();
                Processor.CPU_Core core = CreateDiagnosticCore();
                prepareCore?.Invoke(core);
                ExecuteProgram(ref core, program);

                if (iteration == iterations - 1)
                {
                    validation = validate(core);
                }
            }

            performance = Processor.GetPerformanceStats();
        }
        catch (Exception ex)
        {
            validation = ValidationResult.Failure(ex.Message);
            performance = CapturePerformanceStats();
        }

        stopwatch.Stop();

        return new StreamVectorSpecScenarioReport(
            Id: id,
            Algorithm: algorithm,
            ExecutionSurface: executionSurface,
            WorkloadShape: workloadShape,
            Iterations: iterations,
            StaticInstructionCount: program.Length,
            DynamicInstructionCount: iterations * (ulong)program.Length,
            VectorElementsPerIteration: vectorElementsPerIteration,
            VectorElementsProcessed: iterations * vectorElementsPerIteration,
            ModeledBytesPerIteration: modeledBytesPerIteration,
            ModeledBytesTouched: iterations * modeledBytesPerIteration,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            Passed: validation.Passed,
            FailureMessage: validation.FailureMessage,
            MaxAbsoluteError: validation.MaxAbsoluteError,
            ResultChecksum: validation.ResultChecksum,
            Opcodes: program.Select(static instruction => OpcodeName(instruction.OpCode)).Distinct(StringComparer.Ordinal).ToArray(),
            Telemetry: FromPerformanceReport(performance));
    }

    private static VLIW_Instruction[] BuildSgemmProgram()
    {
        List<VLIW_Instruction> program = new List<VLIW_Instruction>(MatrixN * MatrixN * 3);
        for (int row = 0; row < MatrixN; row++)
        {
            for (int k = 0; k < MatrixN; k++)
            {
                program.Add(EncodeVector(
                    InstructionsEnum.VLOAD,
                    DataTypeEnum.FLOAT32,
                    dest: SgemmTempAddress,
                    src: SgemmBRowAddress(k),
                    length: MatrixN,
                    stride: FloatBytes));

                program.Add(EncodeVector(
                    InstructionsEnum.VMUL,
                    DataTypeEnum.FLOAT32,
                    dest: SgemmTempAddress,
                    src: SgemmABroadcastAddress(row, k),
                    length: MatrixN,
                    stride: FloatBytes));

                program.Add(EncodeVector(
                    InstructionsEnum.VADD,
                    DataTypeEnum.FLOAT32,
                    dest: SgemmCRowAddress(row),
                    src: SgemmTempAddress,
                    length: MatrixN,
                    stride: FloatBytes));
            }
        }

        return program.ToArray();
    }

    private static VLIW_Instruction[] BuildFirProgram()
    {
        VLIW_Instruction[] program = new VLIW_Instruction[FirOutputCount];
        for (int output = 0; output < FirOutputCount; output++)
        {
            program[output] = new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.VDOTF,
                DataTypeValue = DataTypeEnum.FLOAT32,
                PredicateMask = 0,
                DestSrc1Pointer = FirWindowAddressFor(output),
                Src2Pointer = FirTapAddress,
                StreamLength = FirTapCount,
                Stride = FloatBytes
            };
        }

        return program;
    }

    private static VLIW_Instruction[] BuildPredicateCompressionProgram()
    {
        return
        [
            InstructionEncoder.EncodeVectorComparison(
                (uint)InstructionsEnum.VCMPGT,
                DataTypeEnum.UINT32,
                src1Ptr: CompressValuesAddress,
                src2Ptr: CompressThresholdsAddress,
                streamLength: CompressionElementCount,
                destPredicateReg: CompressPredicateRegister,
                stride: WordBytes),
            EncodeVector(
                InstructionsEnum.VCOMPRESS,
                DataTypeEnum.UINT32,
                dest: CompressValuesAddress,
                src: 0,
                length: CompressionElementCount,
                stride: WordBytes,
                predicateMask: CompressPredicateRegister)
        ];
    }

    private static VLIW_Instruction[] BuildCryptoProgram()
    {
        return
        [
            EncodeVector(InstructionsEnum.VXOR, DataTypeEnum.UINT32, CryptoStateAddress, CryptoKeyAddress, CryptoElementCount, WordBytes),
            EncodeVector(InstructionsEnum.VSLL, DataTypeEnum.UINT32, CryptoStateAddress, CryptoShiftLeftAddress, CryptoElementCount, WordBytes),
            EncodeVector(InstructionsEnum.VADD, DataTypeEnum.UINT32, CryptoStateAddress, CryptoAddAddress, CryptoElementCount, WordBytes),
            EncodeVector(InstructionsEnum.VSRL, DataTypeEnum.UINT32, CryptoStateAddress, CryptoShiftRightAddress, CryptoElementCount, WordBytes),
            EncodeVector(InstructionsEnum.VOR, DataTypeEnum.UINT32, CryptoStateAddress, CryptoSaltAddress, CryptoElementCount, WordBytes),
            EncodeVector(InstructionsEnum.VPOPCNT, DataTypeEnum.UINT32, CryptoStateAddress, 0, CryptoElementCount, WordBytes)
        ];
    }

    private static VLIW_Instruction[] BuildStencilProgram()
    {
        List<VLIW_Instruction> program = new List<VLIW_Instruction>(StencilInterior * 4);
        for (int row = 1; row < StencilRows - 1; row++)
        {
            ulong outputRow = StencilOutputCellAddress(row, 1);
            program.Add(EncodeVector(InstructionsEnum.VADD, DataTypeEnum.UINT32, outputRow, StencilGridCellAddress(row - 1, 1), StencilInterior, WordBytes));
            program.Add(EncodeVector(InstructionsEnum.VADD, DataTypeEnum.UINT32, outputRow, StencilGridCellAddress(row + 1, 1), StencilInterior, WordBytes));
            program.Add(EncodeVector(InstructionsEnum.VADD, DataTypeEnum.UINT32, outputRow, StencilGridCellAddress(row, 0), StencilInterior, WordBytes));
            program.Add(EncodeVector(InstructionsEnum.VADD, DataTypeEnum.UINT32, outputRow, StencilGridCellAddress(row, 2), StencilInterior, WordBytes));
        }

        return program.ToArray();
    }

    private static VLIW_Instruction EncodeVector(
        InstructionsEnum opcode,
        DataTypeEnum dataType,
        ulong dest,
        ulong src,
        int length,
        int stride,
        byte predicateMask = 0) =>
        InstructionEncoder.EncodeVector1D(
            (uint)opcode,
            dataType,
            destSrc1Ptr: dest,
            src2Ptr: src,
            streamLength: (ulong)length,
            stride: checked((ushort)stride),
            predicateMask: predicateMask);

    private static void ExecuteProgram(ref Processor.CPU_Core core, IReadOnlyList<VLIW_Instruction> program)
    {
        for (int index = 0; index < program.Count; index++)
        {
            VLIW_Instruction instruction = program[index];
            MicroOp microOp = MaterializeSingleSlotMicroOp(instruction);
            if (microOp is TrapMicroOp trap)
            {
                throw new InvalidOperationException(
                    $"Instruction {index} ({OpcodeName(instruction.OpCode)}) decoded to trap: {trap.GetDescription()}");
            }

            if (microOp is VectorTransferMicroOp)
            {
                ExecuteCanonicalVectorTransfer(ref core, instruction, microOp, index);
                continue;
            }

            if (!microOp.Execute(ref core))
            {
                throw new InvalidOperationException(
                    $"Instruction {index} ({OpcodeName(instruction.OpCode)}) did not complete on the materialized carrier.");
            }
        }
    }

    private static void ExecuteCanonicalVectorTransfer(
        ref Processor.CPU_Core core,
        VLIW_Instruction instruction,
        MicroOp microOp,
        int instructionIndex)
    {
        const int CompletionWatchdogCycles = 8;
        for (int cycle = 0; cycle < CompletionWatchdogCycles; cycle++)
        {
            core.TestRunExecuteStageWithDecodedInstruction(
                instruction,
                microOp,
                isVectorOp: true,
                isMemoryOp: false,
                pc: 0x6_4000UL + (ulong)(instructionIndex * 16));
            if (core.TestReadExecuteStageStatus().ResultReady)
            {
                core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
                return;
            }

            if (Processor.Memory == null)
            {
                throw new InvalidOperationException(
                    "Canonical vector transfer has no bound diagnostic MemorySubsystem.");
            }

            Processor.Memory.AdvanceCycles(1);
        }

        throw new InvalidOperationException(
            $"Instruction {instructionIndex} ({OpcodeName(instruction.OpCode)}) exceeded the canonical vector-transfer completion watchdog.");
    }

    private static MicroOp MaterializeSingleSlotMicroOp(VLIW_Instruction instruction)
    {
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
        rawSlots[0] = instruction;

        VliwDecoderV4 decoder = new VliwDecoderV4();
        DecodedInstructionBundle canonicalBundle =
            decoder.DecodeInstructionBundle(rawSlots, bundleAddress: 0x6_4000, bundleSerial: 117);

        MicroOp?[] carrierBundle =
            DecodedBundleTransportProjector.BuildCanonicalCarrierBundleForTesting(rawSlots, canonicalBundle);

        return carrierBundle[0] ??
               throw new InvalidOperationException(
                   $"Instruction {OpcodeName(instruction.OpCode)} did not materialize a canonical micro-op.");
    }

    private static void SeedSgemmMemory()
    {
        float[] zeroRow = new float[MatrixN];
        for (int row = 0; row < MatrixN; row++)
        {
            WriteFloats(SgemmBRowAddress(row), SgemmB.AsSpan(row * MatrixN, MatrixN));
            WriteFloats(SgemmCRowAddress(row), zeroRow);
        }

        float[] broadcast = new float[MatrixN];
        for (int row = 0; row < MatrixN; row++)
        {
            for (int col = 0; col < MatrixN; col++)
            {
                Array.Fill(broadcast, SgemmA[(row * MatrixN) + col]);
                WriteFloats(SgemmABroadcastAddress(row, col), broadcast);
            }
        }

        WriteFloats(SgemmTempAddress, zeroRow);
    }

    private static void SeedFirMemory()
    {
        WriteFloats(FirInputAddress, FirInput);
        WriteFloats(FirTapAddress, FirTaps);
        for (int output = 0; output < FirOutputCount; output++)
        {
            WriteFloats(FirWindowAddressFor(output), FirInput.AsSpan(output, FirTapCount));
        }
    }

    private static void SeedPredicateCompressionMemory()
    {
        WriteUInt32(CompressValuesAddress, CompressValues);
        WriteUInt32(CompressThresholdsAddress, CompressThresholds);
    }

    private static void SeedCryptoMemory()
    {
        WriteUInt32(CryptoStateAddress, CryptoState);
        WriteUInt32(CryptoKeyAddress, CryptoKey);
        WriteUInt32(CryptoShiftLeftAddress, CryptoShiftLeft);
        WriteUInt32(CryptoAddAddress, CryptoAdd);
        WriteUInt32(CryptoShiftRightAddress, CryptoShiftRight);
        WriteUInt32(CryptoSaltAddress, CryptoSalt);
    }

    private static void SeedStencilMemory()
    {
        uint[] grid = CreateStencilGrid();
        uint[] output = new uint[StencilRows * StencilCols];
        for (int row = 1; row < StencilRows - 1; row++)
        {
            for (int col = 1; col < StencilCols - 1; col++)
            {
                output[(row * StencilCols) + col] = grid[(row * StencilCols) + col];
            }
        }

        WriteUInt32(StencilGridAddress, grid);
        WriteUInt32(StencilOutputAddress, output);
    }

    private static void SeedDmaMemory()
    {
        WriteUInt32(DmaSrcAAddress, DmaSourceA);
        WriteUInt32(DmaSrcBAddress, DmaSourceB);
        WriteUInt32(DmaSrcCAddress, DmaSourceC);
        WriteUInt32(DmaFmaOutputAddress, [0xFACE_0001U, 0xFACE_0002U, 0xFACE_0003U, 0xFACE_0004U]);
        WriteUInt32(DmaReduceOutputAddress, [0xBAD0_BAD0U]);
    }

    private static ValidationResult ValidateSgemm()
    {
        float[] actual = new float[MatrixN * MatrixN];
        for (int row = 0; row < MatrixN; row++)
        {
            ReadFloats(SgemmCRowAddress(row), MatrixN).CopyTo(actual.AsSpan(row * MatrixN, MatrixN));
        }

        double maxAbsError = 0;
        for (int row = 0; row < MatrixN; row++)
        {
            for (int col = 0; col < MatrixN; col++)
            {
                double expected = 0;
                for (int k = 0; k < MatrixN; k++)
                {
                    double product = ApplyArchitecturalVectorFloatRne(
                        SgemmA[(row * MatrixN) + k] * SgemmB[(k * MatrixN) + col]);
                    expected = ApplyArchitecturalVectorFloatRne(expected + product);
                }

                double observed = actual[(row * MatrixN) + col];
                double error = Math.Abs(observed - expected);
                maxAbsError = Math.Max(maxAbsError, error);
                if (error > 0.0001d)
                {
                    return ValidationResult.Failure(
                        $"SGEMM mismatch at ({row},{col}): expected={expected}, actual={observed}.",
                        maxAbsError,
                        ChecksumFloats(actual));
                }
            }
        }

        return ValidationResult.Success(maxAbsError, ChecksumFloats(actual));
    }

    private static ValidationResult ValidateFir()
    {
        float[] actual = new float[FirOutputCount];
        for (int output = 0; output < FirOutputCount; output++)
        {
            actual[output] = ReadFloats(FirWindowAddressFor(output), 1)[0];
        }

        double maxAbsError = 0;
        for (int output = 0; output < FirOutputCount; output++)
        {
            double exact = 0;
            for (int tap = 0; tap < FirTapCount; tap++)
            {
                exact += FirInput[output + tap] * FirTaps[tap];
            }

            double expected = ApplyArchitecturalVectorFloatRne(exact);
            double observed = actual[output];
            double error = Math.Abs(observed - expected);
            maxAbsError = Math.Max(maxAbsError, error);
            if (error > 0.0001d)
            {
                return ValidationResult.Failure(
                    $"FIR mismatch at output {output}: expected={expected}, actual={observed}.",
                    maxAbsError,
                    ChecksumFloats(actual));
            }
        }

        return ValidationResult.Success(maxAbsError, ChecksumFloats(actual));
    }

    private static ValidationResult ValidatePredicateCompression(Processor.CPU_Core core)
    {
        uint[] actual = ReadUInt32(CompressValuesAddress, CompressionElementCount);
        uint[] expectedPrefix = CompressValues
            .Where(static (value, index) => value > CompressThresholds[index])
            .ToArray();

        ulong expectedMask = 0;
        for (int lane = 0; lane < CompressionElementCount; lane++)
        {
            if (CompressValues[lane] > CompressThresholds[lane])
            {
                expectedMask |= 1UL << lane;
            }
        }

        ulong actualMask = core.GetPredicateRegister(CompressPredicateRegister);
        if (actualMask != expectedMask)
        {
            return ValidationResult.Failure(
                $"VCMPGT predicate mismatch: expected=0x{expectedMask:X}, actual=0x{actualMask:X}.",
                0,
                ChecksumUInt32(actual));
        }

        for (int index = 0; index < expectedPrefix.Length; index++)
        {
            if (actual[index] != expectedPrefix[index])
            {
                return ValidationResult.Failure(
                    $"VCOMPRESS prefix mismatch at {index}: expected={expectedPrefix[index]}, actual={actual[index]}.",
                    0,
                    ChecksumUInt32(actual));
            }
        }

        return ValidationResult.Success(0, ChecksumUInt32(actual));
    }

    private static ValidationResult ValidateCrypto()
    {
        uint[] actual = ReadUInt32(CryptoStateAddress, CryptoElementCount);
        uint[] expected = new uint[CryptoElementCount];
        for (int lane = 0; lane < CryptoElementCount; lane++)
        {
            uint value = CryptoState[lane] ^ CryptoKey[lane];
            value = (uint)((ulong)value << (int)(CryptoShiftLeft[lane] & 0x3FU));
            value = unchecked(value + CryptoAdd[lane]);
            value = (uint)((ulong)value >> (int)(CryptoShiftRight[lane] & 0x3FU));
            value |= CryptoSalt[lane];
            expected[lane] = (uint)BitOperations.PopCount(value);
        }

        for (int lane = 0; lane < CryptoElementCount; lane++)
        {
            if (actual[lane] != expected[lane])
            {
                return ValidationResult.Failure(
                    $"Crypto bit-mix mismatch at lane {lane}: expected={expected[lane]}, actual={actual[lane]}.",
                    0,
                    ChecksumUInt32(actual));
            }
        }

        return ValidationResult.Success(0, ChecksumUInt32(actual));
    }

    private static ValidationResult ValidateStencil()
    {
        uint[] grid = CreateStencilGrid();
        uint[] actual = ReadUInt32(StencilOutputAddress, StencilRows * StencilCols);
        for (int row = 1; row < StencilRows - 1; row++)
        {
            for (int col = 1; col < StencilCols - 1; col++)
            {
                uint expected = grid[(row * StencilCols) + col]
                                + grid[((row - 1) * StencilCols) + col]
                                + grid[((row + 1) * StencilCols) + col]
                                + grid[(row * StencilCols) + col - 1]
                                + grid[(row * StencilCols) + col + 1];
                uint observed = actual[(row * StencilCols) + col];
                if (observed != expected)
                {
                    return ValidationResult.Failure(
                        $"Stencil mismatch at ({row},{col}): expected={expected}, actual={observed}.",
                        0,
                        ChecksumUInt32(actual));
                }
            }
        }

        return ValidationResult.Success(0, ChecksumUInt32(actual));
    }

    private static ValidationResult ValidateDmaMicroOpPlacement(DmaStreamComputeDescriptor descriptor)
    {
        DmaStreamComputeMicroOp microOp = new(descriptor);
        if (microOp.Placement.RequiredSlotClass != SlotClass.DmaStreamClass)
        {
            return ValidationResult.Failure(
                $"DmaStreamComputeMicroOp required slot mismatch: expected={SlotClass.DmaStreamClass}, actual={microOp.Placement.RequiredSlotClass}.");
        }

        if (microOp.Class != MicroOpClass.Dma)
        {
            return ValidationResult.Failure(
                $"DmaStreamComputeMicroOp class mismatch: expected={MicroOpClass.Dma}, actual={microOp.Class}.");
        }

        return ValidationResult.Success(0, 0);
    }

    private static ValidationResult ExecuteDmaDescriptor(
        DmaStreamComputeDescriptor descriptor,
        DmaStreamComputeTelemetryCounters telemetryCounters,
        ulong tokenId)
    {
        DmaStreamComputeExecutionResult execution =
            DmaStreamComputeRuntime.ExecuteToCommitPending(descriptor, tokenId, telemetryCounters);
        DmaStreamComputeBackendTelemetry backendTelemetry = execution.Telemetry;
        _lastDmaBackendEvidence = (
            backendTelemetry.ReadBurstCount,
            backendTelemetry.ModeledLatencyCycles,
            backendTelemetry.UsedLane6Backend,
            backendTelemetry.DirectDestinationWriteCount);

        if (!execution.IsCommitPending)
        {
            string reason = execution.Token.LastFault?.Message ?? execution.Completion.TokenState.ToString();
            return ValidationResult.Failure($"DmaStreamCompute did not reach CommitPending: {reason}.");
        }

        if (!backendTelemetry.UsedLane6Backend ||
            backendTelemetry.AluLaneOccupancyDelta != 0 ||
            backendTelemetry.DirectDestinationWriteCount != 0)
        {
            return ValidationResult.Failure(
                $"Dma backend telemetry violates lane6 contract: lane6={backendTelemetry.UsedLane6Backend}, aluDelta={backendTelemetry.AluLaneOccupancyDelta}, directWrites={backendTelemetry.DirectDestinationWriteCount}.");
        }

        Processor.CPU_Core core = CreateDiagnosticCore();
        DmaStreamComputeCommitResult commit =
            core.TestApplyDmaStreamComputeTokenCommit(execution.Token, descriptor.OwnerGuardDecision);
        if (!commit.Succeeded)
        {
            return ValidationResult.Failure(
                $"DmaStreamCompute commit failed: {commit.Fault?.Message ?? commit.TokenState.ToString()}.");
        }

        return ValidationResult.Success(0, 0);
    }

    private static ValidationResult ValidateDmaResults(DmaStreamComputeTelemetryCounters telemetryCounters)
    {
        uint[] fma = ReadUInt32(DmaFmaOutputAddress, 4);
        uint[] reduce = ReadUInt32(DmaReduceOutputAddress, 1);
        uint[] expectedFma = [17U, 48U, 99U, 170U];
        for (int lane = 0; lane < expectedFma.Length; lane++)
        {
            if (fma[lane] != expectedFma[lane])
            {
                return ValidationResult.Failure(
                    $"Dma FMA mismatch at lane {lane}: expected={expectedFma[lane]}, actual={fma[lane]}.",
                    0,
                    ChecksumUInt32(fma));
            }
        }

        if (reduce[0] != 10U)
        {
            return ValidationResult.Failure(
                $"Dma reduce mismatch: expected=10, actual={reduce[0]}.",
                0,
                ChecksumUInt32(reduce));
        }

        DmaStreamComputeTelemetrySnapshot snapshot = telemetryCounters.Snapshot();
        if (snapshot.ComputeCommitted != 2 || snapshot.BytesStaged != 20 || snapshot.ElementOperations != 8)
        {
            return ValidationResult.Failure(
                $"Dma telemetry mismatch: committed={snapshot.ComputeCommitted}, staged={snapshot.BytesStaged}, elementOps={snapshot.ElementOperations}.",
                0,
                ChecksumUInt32(fma) ^ ChecksumUInt32(reduce));
        }

        return ValidationResult.Success(0, ChecksumUInt32(fma) ^ ChecksumUInt32(reduce));
    }

    private static double ApplyArchitecturalVectorFloatRne(double value) =>
        Math.Round(value, MidpointRounding.ToEven);

    private static Processor.CPU_Core CreateDiagnosticCore() =>
        DiagnosticShadowFrontend.ConfigureNewCore(new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Emulation)));

    private static DmaStreamComputeDescriptor CreateDmaDescriptor(
        DmaStreamComputeOperationKind operation,
        DmaStreamComputeShapeKind shape,
        DmaStreamComputeMemoryRange[] readRanges,
        DmaStreamComputeMemoryRange[] writeRanges,
        ulong descriptorOrdinal)
    {
        DmaStreamComputeOwnerBinding ownerBinding = new DmaStreamComputeOwnerBinding
        {
            OwnerVirtualThreadId = 1,
            OwnerContextId = 77,
            OwnerCoreId = 1,
            OwnerPodId = 2,
            OwnerDomainTag = 0xD0A11,
            DeviceId = DmaStreamComputeDescriptor.CanonicalLane6DeviceId
        };

        DmaStreamComputeOwnerGuardContext ownerContext = new DmaStreamComputeOwnerGuardContext(
            ownerBinding.OwnerVirtualThreadId,
            ownerBinding.OwnerContextId,
            ownerBinding.OwnerCoreId,
            ownerBinding.OwnerPodId,
            ownerBinding.OwnerDomainTag,
            activeDomainCertificate: ownerBinding.OwnerDomainTag,
            deviceId: ownerBinding.DeviceId);

        DmaStreamComputeOwnerGuardDecision guardDecision =
            new SafetyVerifier().EvaluateDmaStreamComputeOwnerGuard(ownerBinding, ownerContext);

        ulong identityHash = DmaIdentityHash + descriptorOrdinal;
        return new DmaStreamComputeDescriptor
        {
            DescriptorReference = new DmaStreamComputeDescriptorReference(
                descriptorAddress: DmaDescriptorAddress + (descriptorOrdinal * 0x100UL),
                descriptorSize: DmaStreamComputeDescriptorParser.CurrentHeaderSize,
                descriptorIdentityHash: identityHash),
            AbiVersion = DmaStreamComputeDescriptorParser.CurrentAbiVersion,
            HeaderSize = DmaStreamComputeDescriptorParser.CurrentHeaderSize,
            TotalSize = DmaStreamComputeDescriptorParser.CurrentHeaderSize,
            DescriptorIdentityHash = identityHash,
            CertificateInputHash = 0xC011_EC7EUL + descriptorOrdinal,
            Operation = operation,
            ElementType = DmaStreamComputeElementType.UInt32,
            Shape = shape,
            RangeEncoding = DmaStreamComputeRangeEncoding.InlineContiguous,
            PartialCompletionPolicy = DmaStreamComputePartialCompletionPolicy.AllOrNone,
            OwnerBinding = ownerBinding,
            OwnerGuardDecision = guardDecision,
            ReadMemoryRanges = readRanges,
            NormalizedReadMemoryRanges = Normalize(readRanges),
            WriteMemoryRanges = writeRanges,
            NormalizedWriteMemoryRanges = Normalize(writeRanges),
            AliasPolicy = DmaStreamComputeAliasPolicy.Disjoint,
            NormalizedFootprintHash = 0xF007_F007UL + descriptorOrdinal
        };
    }

    private static DmaStreamComputeMemoryRange[] Normalize(DmaStreamComputeMemoryRange[] ranges)
    {
        if (ranges.Length == 0)
        {
            return [];
        }

        DmaStreamComputeMemoryRange[] sorted = (DmaStreamComputeMemoryRange[])ranges.Clone();
        Array.Sort(sorted, static (left, right) =>
        {
            int addressCompare = left.Address.CompareTo(right.Address);
            return addressCompare != 0 ? addressCompare : left.Length.CompareTo(right.Length);
        });

        List<DmaStreamComputeMemoryRange> normalized = new List<DmaStreamComputeMemoryRange>();
        ulong currentStart = sorted[0].Address;
        ulong currentEnd = sorted[0].Address + sorted[0].Length;
        for (int i = 1; i < sorted.Length; i++)
        {
            ulong nextEnd = sorted[i].Address + sorted[i].Length;
            if (sorted[i].Address <= currentEnd)
            {
                if (nextEnd > currentEnd)
                {
                    currentEnd = nextEnd;
                }

                continue;
            }

            normalized.Add(new DmaStreamComputeMemoryRange(currentStart, currentEnd - currentStart));
            currentStart = sorted[i].Address;
            currentEnd = nextEnd;
        }

        normalized.Add(new DmaStreamComputeMemoryRange(currentStart, currentEnd - currentStart));
        return normalized.ToArray();
    }

    private static uint[] CreateStencilGrid()
    {
        uint[] grid = new uint[StencilRows * StencilCols];
        for (int row = 0; row < StencilRows; row++)
        {
            for (int col = 0; col < StencilCols; col++)
            {
                grid[(row * StencilCols) + col] = (uint)(10 + (row * 7) + (col * 3) + ((row * col) % 5));
            }
        }

        return grid;
    }

    private static void WriteFloats(ulong address, ReadOnlySpan<float> values)
    {
        byte[] bytes = new byte[checked(values.Length * FloatBytes)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(i * FloatBytes, FloatBytes),
                BitConverter.SingleToInt32Bits(values[i]));
        }

        Processor.MainMemory.WriteToPosition(bytes, address);
    }

    private static float[] ReadFloats(ulong address, int count)
    {
        byte[] bytes = Processor.MainMemory.ReadFromPosition(new byte[checked(count * FloatBytes)], address, (ulong)(count * FloatBytes));
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * FloatBytes, FloatBytes)));
        }

        return values;
    }

    private static void WriteUInt32(ulong address, ReadOnlySpan<uint> values)
    {
        byte[] bytes = new byte[checked(values.Length * WordBytes)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * WordBytes, WordBytes), values[i]);
        }

        Processor.MainMemory.WriteToPosition(bytes, address);
    }

    private static uint[] ReadUInt32(ulong address, int count)
    {
        byte[] bytes = Processor.MainMemory.ReadFromPosition(new byte[checked(count * WordBytes)], address, (ulong)(count * WordBytes));
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * WordBytes, WordBytes));
        }

        return values;
    }

    private static ulong SgemmABroadcastAddress(int row, int k) =>
        SgemmAAddress + (ulong)(((row * MatrixN) + k) * MatrixN * FloatBytes);

    private static ulong SgemmBRowAddress(int row) =>
        SgemmBAddress + (ulong)(row * MatrixN * FloatBytes);

    private static ulong SgemmCRowAddress(int row) =>
        SgemmCAddress + (ulong)(row * MatrixN * FloatBytes);

    private static ulong FirWindowAddressFor(int output) =>
        FirWindowAddress + (ulong)(output * 0x100);

    private static ulong StencilGridCellAddress(int row, int col) =>
        StencilGridAddress + (ulong)(((row * StencilCols) + col) * WordBytes);

    private static ulong StencilOutputCellAddress(int row, int col) =>
        StencilOutputAddress + (ulong)(((row * StencilCols) + col) * WordBytes);

    private static StreamVectorSpecTelemetry FromPerformanceReport(PerformanceReport report) =>
        new(
            TotalBursts: report.TotalBursts,
            TotalBytesTransferred: report.TotalBytesTransferred,
            ForegroundWarmAttempts: report.ForegroundWarmAttempts,
            ForegroundWarmSuccesses: report.ForegroundWarmSuccesses,
            ForegroundWarmReuseHits: report.ForegroundWarmReuseHits,
            ForegroundBypassHits: report.ForegroundBypassHits,
            AssistWarmAttempts: report.AssistWarmAttempts,
            AssistWarmSuccesses: report.AssistWarmSuccesses,
            AssistWarmReuseHits: report.AssistWarmReuseHits,
            AssistBypassHits: report.AssistBypassHits,
            StreamWarmTranslationRejects: report.StreamWarmTranslationRejects,
            StreamWarmBackendRejects: report.StreamWarmBackendRejects,
            DmaBytesRead: 0,
            DmaBytesStaged: 0,
            DmaReadBurstCount: 0,
            DmaModeledLatencyCycles: 0,
            DmaUsedLane6Backend: false,
            DmaDirectDestinationWrites: 0,
            DmaElementOperations: 0);

    private static PerformanceReport CapturePerformanceStats()
    {
        try
        {
            return Processor.GetPerformanceStats();
        }
        catch
        {
            return new PerformanceReport();
        }
    }

    private static StreamVectorSpecAggregate BuildAggregate(IReadOnlyList<StreamVectorSpecScenarioReport> scenarios)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (StreamVectorSpecScenarioReport scenario in scenarios)
        {
            checksum = MixChecksum(checksum, scenario.ResultChecksum);
        }

        return new StreamVectorSpecAggregate(
            ScenarioCount: scenarios.Count,
            PassedScenarioCount: scenarios.Count(static scenario => scenario.Passed),
            DynamicInstructionCount: Sum(scenarios, static scenario => scenario.DynamicInstructionCount),
            VectorElementsProcessed: Sum(scenarios, static scenario => scenario.VectorElementsProcessed),
            ModeledBytesTouched: Sum(scenarios, static scenario => scenario.ModeledBytesTouched),
            ElapsedMilliseconds: scenarios.Sum(static scenario => scenario.ElapsedMilliseconds),
            ResultChecksum: checksum);
    }

    private static ulong Sum(
        IEnumerable<StreamVectorSpecScenarioReport> scenarios,
        Func<StreamVectorSpecScenarioReport, ulong> selector)
    {
        ulong total = 0;
        foreach (StreamVectorSpecScenarioReport scenario in scenarios)
        {
            total += selector(scenario);
        }

        return total;
    }

    private static ulong ChecksumFloats(ReadOnlySpan<float> values)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (float value in values)
        {
            checksum = MixChecksum(checksum, (uint)BitConverter.SingleToInt32Bits(value));
        }

        return checksum;
    }

    private static ulong ChecksumUInt32(ReadOnlySpan<uint> values)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (uint value in values)
        {
            checksum = MixChecksum(checksum, value);
        }

        return checksum;
    }

    private static ulong MixChecksum(ulong checksum, ulong value) =>
        unchecked((checksum ^ value) * 1_099_511_628_211UL);

    private static string OpcodeName(uint opcode) =>
        Enum.IsDefined(typeof(InstructionsEnum), (ushort)opcode)
            ? ((InstructionsEnum)(ushort)opcode).ToString()
            : $"0x{opcode:X}";

    private static void BootstrapRuntime()
    {
#pragma warning disable CS0618
        _ = new Processor(ProcessorMode.Compiler);
#pragma warning restore CS0618
        DiagnosticShadowFrontend.ConfigureBootstrappedCores();

        Processor.CurrentProcessorMode = ProcessorMode.Emulation;
        Processor.ConfigureProfiling(true, ProfilingOptions.Default());
        Processor.ResetPerformanceCounters();
    }

    private readonly record struct ValidationResult(
        bool Passed,
        double MaxAbsoluteError,
        ulong ResultChecksum,
        string FailureMessage)
    {
        public static ValidationResult Success(double maxAbsoluteError, ulong resultChecksum) =>
            new(true, maxAbsoluteError, resultChecksum, string.Empty);

        public static ValidationResult Failure(
            string message,
            double maxAbsoluteError = 0,
            ulong resultChecksum = 0) =>
            new(false, maxAbsoluteError, resultChecksum, message);
    }
}
