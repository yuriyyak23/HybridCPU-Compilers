using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record MatrixTileSpecSuiteReport(
    string SuiteId,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    ulong Iterations,
    string RuntimeClosureDecision,
    IReadOnlyList<string> ArchitectureKeys,
    IReadOnlyList<MatrixTileSpecScenarioReport> Scenarios,
    MatrixTileSpecAggregate Aggregate)
{
    public bool Succeeded => Scenarios.All(static scenario => scenario.Passed);
}

internal sealed record MatrixTileSpecAggregate(
    int ScenarioCount,
    int PassedScenarioCount,
    ulong RuntimeInstructionCount,
    ulong CompilerEmissionCount,
    ulong RetirePublicationCount,
    ulong ReplayRoundTripCount,
    ulong FailClosedRejectionCount,
    ulong StreamBytesTransferred,
    ulong StreamInvalidationCount,
    double RuntimeInstructionsPerMillisecond,
    double StreamBytesPerMillisecond,
    double RetirePublicationsPerMillisecond,
    double ReplayRoundTripsPerMillisecond,
    bool MeetsSmokeThroughputBaseline,
    double ElapsedMilliseconds,
    ulong ResultChecksum);

internal sealed record MatrixTileSpecScenarioReport(
    string Id,
    string Contour,
    string WorkloadShape,
    ulong Iterations,
    ulong RuntimeInstructionCount,
    ulong CompilerEmissionCount,
    ulong RetirePublicationCount,
    ulong ReplayRoundTripCount,
    ulong FailClosedRejectionCount,
    ulong StreamBytesTransferred,
    ulong StreamInvalidationCount,
    double RuntimeInstructionsPerMillisecond,
    double StreamBytesPerMillisecond,
    double RetirePublicationsPerMillisecond,
    double ReplayRoundTripsPerMillisecond,
    bool MeetsSmokeThroughputBaseline,
    IReadOnlyList<string> Opcodes,
    IReadOnlyList<string> ResourceEvidence,
    double ElapsedMilliseconds,
    bool Passed,
    string FailureMessage,
    ulong ResultChecksum);

internal sealed class MatrixTileSpecSuite
{
    private const ulong LoadSourceBaseAddress = 0x100UL;
    private const ulong StoreDestinationBaseAddress = 0x4000UL;
    private const ulong AddressStride = 16UL;
    private const ushort MatrixSide = 2;

    public MatrixTileSpecSuiteReport Execute(ulong iterations)
    {
        if (iterations == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "Iterations must be positive.");
        }

        BootstrapRuntime();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        MatrixTileSpecScenarioReport[] scenarios =
        [
            RunMemoryRoundTripPressure(iterations),
            RunMemoryContourVariedShapePressure(iterations),
            RunLane6SchedulerConflictPressure(),
            RunMaccPolicyReplayPressure(iterations),
            RunNumericLayoutAbiPressure(),
            RunGoldenManifestCoveragePressure(),
            RunGoldenJsonCorpusLoaderPressure(),
            RunTransposePolicyReplayPressure(iterations),
            RunMemoryFaultAllOrNonePressure(),
            RunLoadMemoryFaultNoPublicationPressure(),
            RunCompilerSidebandLoweringConformance(),
            RunCompilerLoweredRuntimeExecutionPressure(),
            RunFullPipelineE2ePressure(),
            RunProductionStageFlowE2ePressure(),
            RunProductionPcFetchStageChainPressure(),
            RunFailClosedPressure(),
            RunFaultFuzzPressure()
        ];
        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;

        return new MatrixTileSpecSuiteReport(
            SuiteId: "matrix-tile-spec-pressure-suite",
            StartedUtc: startedUtc,
            FinishedUtc: finishedUtc,
            Iterations: iterations,
            RuntimeClosureDecision: MatrixTileRuntimeIsaPackageContract.Phase19CompilerConformanceDecision,
            ArchitectureKeys:
            [
                "MTILE_LOAD and MTILE_STORE use typed MatrixTile stream transport on lane6; DmaStreamClass is an aliased capacity conflict.",
                "MTILE_MACC owns explicit runtime numeric/layout policies; MTRANSPOSE owns explicit runtime layout policy without a MACC numeric policy.",
                "Numeric/layout conformance spans signed integer widening/overflow, binary32/binary64 software IEEE results, and operation-specific layout validation.",
                "Execute captures are invisible until retire; rollback/replay use policy-bound core-owned identity and checkpoints.",
                "Tampered numeric/layout or typed-transfer identity is rejected before publication; compiler metadata is not runtime authority."
            ],
            Scenarios: scenarios,
            Aggregate: BuildAggregate(scenarios));
    }

    private static MatrixTileSpecScenarioReport RunMemoryRoundTripPressure(ulong iterations)
    {
        const string id = "mtile-memory-lane6-roundtrip-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Processor.MainMemoryArea memory = CreateMemory(iterations);
            Processor.CPU_Core core = CreateCore(memory);

            for (ulong iteration = 0; iteration < iterations; iteration++)
            {
                ushort tileId = checked((ushort)(iteration + 1));
                ulong sourceAddress = checked(LoadSourceBaseAddress + (iteration * AddressStride));
                ulong destinationAddress = checked(StoreDestinationBaseAddress + (iteration * AddressStride));
                byte[] expected = CreatePayload(iteration);
                WriteMemory(memory, sourceAddress, expected);

                MatrixTileMicroOp load = CreateMemoryMicroOp(
                    InstructionsEnum.MTILE_LOAD,
                    sourceAddress,
                    tileId);
                Require(!core.TryCaptureAnyMatrixTileSnapshot(0, tileId, out _),
                    "Load destination tile was visible before execute/retire.");
                MatrixTileExecutionCaptureRecord loadCapture = ExecuteAndGetCapture(ref core, load);
                Require(loadCapture.StreamTransfer.IsTypedTransport &&
                        loadCapture.StreamTransfer.Direction == MatrixTileStreamTransferDirection.MemoryIngress &&
                        loadCapture.StreamTransfer.Windows.Length == MatrixSide,
                    "MTILE_LOAD did not produce a completed typed lane6 ingress transfer.");
                Require(!core.TryCaptureAnyMatrixTileSnapshot(0, tileId, out _),
                    "MTILE_LOAD execute capture published architectural tile state before retire.");
                RetireAndReplay(ref core, load, loadCapture, expected, tileId, load.ResultTileDescriptor, ref counters);

                MatrixTileMicroOp store = CreateMemoryMicroOp(
                    InstructionsEnum.MTILE_STORE,
                    destinationAddress,
                    tileId);
                MatrixTileExecutionCaptureRecord storeCapture = ExecuteAndGetCapture(ref core, store);
                Require(storeCapture.StreamTransfer.IsTypedTransport &&
                        storeCapture.StreamTransfer.Direction == MatrixTileStreamTransferDirection.TileEgress,
                    "MTILE_STORE did not produce a completed typed lane6 egress transfer.");
                Require(ReadMemory(memory, destinationAddress, expected.Length).All(static value => value == 0),
                    "MTILE_STORE mutated memory before retire.");

                ulong invalidationsBefore = core.MatrixTileStreamInvalidationCount;
                MatrixTileRetireOutcome storeRetire = store.RetireCapturedResult(ref core, storeCapture);
                Require(storeRetire.IsSuccess && storeRetire.CommittedMemory,
                    "MTILE_STORE did not complete an all-or-none retire commit.");
                Require(ReadMemory(memory, destinationAddress, expected.Length).SequenceEqual(expected),
                    "MTILE_STORE retire image does not match the loaded tile image.");
                Require(core.MatrixTileStreamInvalidationCount > invalidationsBefore,
                    "MTILE_STORE retire did not invalidate overlapping MatrixTile SRF windows.");
                counters.RetirePublicationCount++;
                counters.StreamInvalidationCount += core.MatrixTileStreamInvalidationCount - invalidationsBefore;

                MatrixTileReplayRollbackJournal storeJournal = RequireJournal(store);
                MatrixTileRollbackOutcome storeRollback = store.RollbackRetiredResult(ref core, storeJournal.ReplayIdentity);
                Require(storeRollback.RestoredMemory,
                    "MTILE_STORE rollback did not restore the core-owned memory checkpoint.");
                Require(ReadMemory(memory, destinationAddress, expected.Length).All(static value => value == 0),
                    "MTILE_STORE rollback left a partial memory image.");
                MatrixTileRetireOutcome storeReplay = store.ReplayRolledBackResult(ref core, storeJournal.ReplayIdentity);
                Require(storeReplay.IsSuccess && storeReplay.CommittedMemory,
                    "MTILE_STORE replay did not restore the retire-owned commit.");
                Require(ReadMemory(memory, destinationAddress, expected.Length).SequenceEqual(expected),
                    "MTILE_STORE replay image does not match the staged write set.");

                counters.RuntimeInstructionCount += 2;
                counters.ReplayRoundTripCount++;
                counters.StreamBytesTransferred += checked((ulong)(expected.Length * 2));
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(expected));
            }

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileMemory / MatrixTileStreamClass / lane6",
                "Repeated 2x2 typed ingress and egress with retire-only publication, all-or-none store rollback/replay, and SRF invalidation.",
                iterations,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE"],
                [
                    "resource=MatrixTileMemory",
                    "slot=MatrixTileStreamClass",
                    "lane=6",
                    "channel=0",
                    "DmaStreamClass capacity conflict verified by runtime lane map"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileMemory / MatrixTileStreamClass / lane6", iterations, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunMemoryContourVariedShapePressure(ulong iterations)
    {
        const string id = "mtile-memory-contour-varied-shape-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            (ushort Rows, ushort Columns, uint RowStride, ulong SourceOffset, ulong DestinationOffset)[] shapes =
            [
                (1, 4, 4, 0x000, 0x000),
                (2, 3, 4, 0x080, 0x080),
                (3, 2, 3, 0x100, 0x100),
                (4, 1, 2, 0x180, 0x180)
            ];
            Processor.MainMemoryArea memory = CreateMemory(checked(iterations + (ulong)shapes.Length));
            Processor.CPU_Core core = CreateCore(memory);

            for (ulong iteration = 0; iteration < iterations; iteration++)
            {
                for (int shapeIndex = 0; shapeIndex < shapes.Length; shapeIndex++)
                {
                    (ushort rows, ushort columns, uint rowStride, ulong sourceOffset, ulong destinationOffset) = shapes[shapeIndex];
                    ushort tileId = checked((ushort)(800 + (iteration * (ulong)shapes.Length) + (ulong)shapeIndex));
                    ulong sourceAddress = checked(LoadSourceBaseAddress + sourceOffset + (iteration * 0x20UL));
                    ulong destinationAddress = checked(StoreDestinationBaseAddress + destinationOffset + (iteration * 0x20UL));
                    MatrixTileMicroOp load = CreateMemoryMicroOp(
                        InstructionsEnum.MTILE_LOAD,
                        sourceAddress,
                        tileId,
                        rows,
                        columns,
                        rowStride);
                    int payloadLength = MatrixTileExecuteCaptureAbi.GetPackedByteLength(load.ResultTileDescriptor);
                    byte[] expected = CreatePatternedPayload(payloadLength, iteration, shapeIndex);
                    byte[] stridedMemoryImage = CreateStridedMemoryPayload(expected, rows, columns, rowStride);
                    byte[] expectedStoreImage = CreateStridedMemoryPayload(expected, rows, columns, rowStride);
                    WriteMemory(memory, sourceAddress, stridedMemoryImage);
                    WriteMemory(memory, destinationAddress, expectedStoreImage);

                    MatrixTileExecutionCaptureRecord loadCapture = ExecuteAndGetCapture(ref core, load);
                    Require(loadCapture.StreamTransfer.Windows.Length == rows,
                        $"{id}: MTILE_LOAD did not expose one SRF row window per shape row.");
                    Require(!core.TryCaptureAnyMatrixTileSnapshot(0, tileId, out _),
                        $"{id}: varied-shape load published before retire.");
                    RetireAndReplay(ref core, load, loadCapture, expected, tileId, load.ResultTileDescriptor, ref counters);

                    MatrixTileMicroOp store = CreateMemoryMicroOp(
                        InstructionsEnum.MTILE_STORE,
                        destinationAddress,
                        tileId,
                        rows,
                        columns,
                        rowStride);
                    MatrixTileExecutionCaptureRecord storeCapture = ExecuteAndGetCapture(ref core, store);
                    ulong invalidationsBefore = core.MatrixTileStreamInvalidationCount;
                    MatrixTileRetireOutcome storeRetire = store.RetireCapturedResult(ref core, storeCapture);
                    Require(storeRetire.IsSuccess && storeRetire.CommittedMemory,
                        $"{id}: varied-shape store did not commit all-or-none.");
                    Require(ReadMemory(memory, destinationAddress, expectedStoreImage.Length).SequenceEqual(expectedStoreImage),
                        $"{id}: varied-shape store changed stride padding instead of preserving the pre-store image.");
                    Require(core.MatrixTileStreamInvalidationCount > invalidationsBefore,
                        $"{id}: varied-shape store did not invalidate overlapping SRF windows.");

                    counters.RuntimeInstructionCount += 2;
                    counters.RetirePublicationCount++;
                    counters.StreamInvalidationCount += core.MatrixTileStreamInvalidationCount - invalidationsBefore;
                    counters.StreamBytesTransferred += checked((ulong)(expected.Length * 2));
                    counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(expected));
                }
            }

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileMemory / varied descriptor shapes and SRF row windows",
                "1x4, 2x3, 3x2, and 4x1 typed MatrixTile load/store pressure with non-square row-window identities, row strides, overlap invalidation, and retire-only publication.",
                checked(iterations * (ulong)shapes.Length),
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE"],
                [
                    "shape=1x4/2x3/3x2/4x1",
                    "stride=canonical and padded row windows",
                    "publication=load retire only",
                    "store=all-or-none commit plus SRF invalidation"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileMemory / varied descriptor shapes and SRF row windows", iterations, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunLane6SchedulerConflictPressure()
    {
        const string id = "mtile-lane6-scheduler-conflict-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            SlotClass[] candidateClasses =
            [
                SlotClass.DmaStreamClass,
                SlotClass.MatrixTileStreamClass,
                SlotClass.AluClass,
                SlotClass.LsuClass
            ];

            for (int round = 0; round < candidateClasses.Length * 3; round++)
            {
                SlotClass candidateClass = candidateClasses[round % candidateClasses.Length];
                MicroOpScheduler scheduler = new MicroOpScheduler
                {
                    TypedSlotEnabled = true
                };
                MatrixTileMicroOp matrixTileLoad = CreateMemoryMicroOp(
                    InstructionsEnum.MTILE_LOAD,
                    checked(LoadSourceBaseAddress + (ulong)(round * 0x10)),
                    checked((ushort)(700 + round)));
                TestOnlySlotClassClaimMicroOp candidate = new TestOnlySlotClassClaimMicroOp(
                    candidateClass,
                    SelectLaneForClass(candidateClass),
                    $"TestOnly{candidateClass}Claim");
                MicroOp?[] bundle = new MicroOp?[BundleMetadata.BundleSlotCount];
                bundle[MatrixTileResourceContour.TileStreamLaneId] = matrixTileLoad;

                scheduler.NominateSmtCandidate(1, candidate);
                MicroOp[] packed = scheduler.PackBundleIntraCoreSmt(
                    bundle,
                    ownerVirtualThreadId: 0,
                    localCoreId: 0,
                    eligibleVirtualThreadMask: 0b_0010);

                Require(ReferenceEquals(packed[MatrixTileResourceContour.TileStreamLaneId], matrixTileLoad),
                    "Scheduler displaced the foreground MatrixTile lane6 memory operation.");
                bool isLane6Alias = candidateClass is SlotClass.DmaStreamClass or SlotClass.MatrixTileStreamClass;
                bool injected = packed.Any(op => ReferenceEquals(op, candidate));
                if (isLane6Alias)
                {
                    Require(!injected, $"Scheduler injected {candidateClass} while MatrixTileStreamClass occupied lane6.");
                    Require(scheduler.SmtRejectionsCount > 0 ||
                            scheduler.SmtLegalityRejectByDmaStreamClass > 0 ||
                            scheduler.SmtLegalityRejectByMatrixTileStreamClass > 0,
                        $"Scheduler did not record a lane6 SMT rejection for {candidateClass} conflict.");
                    counters.FailClosedRejectionCount++;
                }
                else
                {
                    Require(injected, $"Scheduler rejected independent {candidateClass} candidate despite free non-lane6 capacity.");
                }

                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)candidateClass);
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)scheduler.SmtRejectionsCount);
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)scheduler.SmtLegalityRejectByDmaStreamClass);
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)scheduler.SmtLegalityRejectByMatrixTileStreamClass);
            }

            stopwatch.Stop();
            return Success(
                id,
                "Scheduler / lane6 MatrixTileStreamClass capacity pressure",
                "Repeated real MicroOpScheduler SMT pack attempts keep foreground MatrixTile memory on lane6, reject lane6 stream aliases, and still admit independent ALU/LSU competitors.",
                checked((ulong)(candidateClasses.Length * 3)),
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "TEST_SLOT_CLASS_CLAIM"],
                [
                    "foreground=MatrixTileStreamClass/lane6",
                    "candidate=DmaStreamClass/lane6 rejected",
                    "candidate=MatrixTileStreamClass/lane6 rejected",
                    "candidate=AluClass/LsuClass admitted on independent capacity"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Scheduler / lane6 MatrixTileStreamClass-vs-DmaStreamClass conflict", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunMaccPolicyReplayPressure(ulong iterations)
    {
        const string id = "mtile-macc-numeric-policy-replay-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Processor.MainMemoryArea memory = CreateMemory(iterations);
            Processor.CPU_Core core = CreateCore(memory);
            byte[] expected = PackInt32(1, 2, 3, 4);

            for (ulong iteration = 0; iteration < iterations; iteration++)
            {
                ushort leftTileId = checked((ushort)(10_000UL + (iteration * 3)));
                ushort rightTileId = checked((ushort)(leftTileId + 1));
                ushort accumulatorTileId = checked((ushort)(leftTileId + 2));
                MatrixTileMicroOp macc = CreateMaccMicroOp(leftTileId, rightTileId, accumulatorTileId);

                core.SeedMatrixTileForRuntime(0, leftTileId, macc.TileDescriptor, [1, 2, 3, 4]);
                core.SeedMatrixTileForRuntime(0, rightTileId, macc.SecondaryTileDescriptor, [1, 0, 0, 1]);
                core.SeedMatrixTileForRuntime(
                    0,
                    accumulatorTileId,
                    macc.ResultTileDescriptor,
                    new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(macc.ResultTileDescriptor)]);

                MatrixTileExecutionCaptureRecord capture = ExecuteAndGetCapture(ref core, macc);
                Require(capture.NumericPolicy.HasValue && capture.LayoutPolicy.HasValue &&
                        MatrixTilePolicyBoundIdentityAbi.ValidateCapture(
                            capture,
                            core.GetMatrixTileReplayInvalidationEpoch()),
                    "MTILE_MACC capture did not bind validated numeric/layout policy identity.");
                RetireAndReplay(ref core, macc, capture, expected, accumulatorTileId, macc.ResultTileDescriptor, ref counters);

                counters.RuntimeInstructionCount++;
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, capture.PolicyIdentity.IdentityFingerprint);
            }

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileCompute / AluClass",
                "Repeated INT8-to-INT32 row-major MACC with explicit numeric/layout sidebands and policy-bound retire/rollback/replay identity.",
                iterations,
                counters,
                stopwatch.Elapsed,
                ["MTILE_MACC"],
                [
                    "resource=MatrixTileCompute",
                    "slot=AluClass",
                    "numeric=SignedInt8ToInt32",
                    "layout=MaccCanonicalRowMajorAscendingK",
                    "publication=Accumulator"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileCompute / AluClass", iterations, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunTransposePolicyReplayPressure(ulong iterations)
    {
        const string id = "mtranspose-layout-policy-replay-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Processor.MainMemoryArea memory = CreateMemory(iterations);
            Processor.CPU_Core core = CreateCore(memory);
            byte[] expected = [1, 3, 2, 4];

            for (ulong iteration = 0; iteration < iterations; iteration++)
            {
                ushort sourceTileId = checked((ushort)(30_000UL + (iteration * 2)));
                ushort destinationTileId = checked((ushort)(sourceTileId + 1));
                MatrixTileMicroOp transpose = CreateTransposeMicroOp(sourceTileId, destinationTileId);
                core.SeedMatrixTileForRuntime(0, sourceTileId, transpose.TileDescriptor, [1, 2, 3, 4]);

                MatrixTileExecutionCaptureRecord capture = ExecuteAndGetCapture(ref core, transpose);
                Require(!capture.NumericPolicy.HasValue && capture.LayoutPolicy.HasValue &&
                        MatrixTilePolicyBoundIdentityAbi.ValidateCapture(
                            capture,
                            core.GetMatrixTileReplayInvalidationEpoch()),
                    "MTRANSPOSE capture did not bind its layout-only policy identity.");
                RetireAndReplay(ref core, transpose, capture, expected, destinationTileId, transpose.ResultTileDescriptor, ref counters);

                counters.RuntimeInstructionCount++;
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, capture.PolicyIdentity.IdentityFingerprint);
            }

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileCompute / AluClass",
                "Repeated row-major coordinate permutation with layout-only sideband and destination tile rollback/replay.",
                iterations,
                counters,
                stopwatch.Elapsed,
                ["MTRANSPOSE"],
                [
                    "resource=MatrixTileCompute",
                    "slot=AluClass",
                    "numeric=absent by operation contract",
                    "layout=TransposeCanonicalRowMajor",
                    "publication=TileState"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileCompute / AluClass", iterations, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunNumericLayoutAbiPressure()
    {
        const string id = "mtile-numeric-layout-abi-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            VerifyMaccProfile(
                MatrixTileNumericProfileId.SignedInt8ToInt32,
                [(byte)0xFE],
                [0x03],
                PackInt32(10),
                PackInt32(4),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.UnsignedInt8ToUInt32,
                [0xFE],
                [0x03],
                PackUInt32(10),
                PackUInt32(772),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.SignedInt16ToInt32,
                PackInt16(258),
                PackInt16(1),
                PackInt32(0),
                PackInt32(258),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.UnsignedInt16ToUInt32,
                PackUInt16(0xFFFE),
                PackUInt16(2),
                PackUInt32(1),
                PackUInt32(131069),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.SignedInt32ToInt64,
                PackInt32(-2),
                PackInt32(3),
                PackInt64(10),
                PackInt64(4),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.UnsignedInt32ToUInt64,
                PackUInt32(0xFFFFFFFE),
                PackUInt32(2),
                PackUInt64(1),
                PackUInt64(8589934589UL),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.SignedInt64ToInt64,
                PackInt64(-2),
                PackInt64(3),
                PackInt64(10),
                PackInt64(4),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.UnsignedInt64ToUInt64,
                PackUInt64(2),
                PackUInt64(3),
                PackUInt64(10),
                PackUInt64(16),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.Binary32ToBinary32,
                PackUInt32(0x3F800001),
                PackUInt32(0x3F7FFFFE),
                PackUInt32(0xBF800000),
                PackUInt32(0x00000000),
                ref counters);
            VerifyMaccProfile(
                MatrixTileNumericProfileId.Binary64ToBinary64,
                PackUInt64(0x3FF8000000000000),
                PackUInt64(0x4000000000000000),
                PackUInt64(0x0000000000000000),
                PackUInt64(0x4008000000000000),
                ref counters);

            MatrixTileMaccSemanticContract signedInt16 = CreateMaccContract(
                MatrixTileNumericProfileId.SignedInt16ToInt32,
                rows: 1,
                k: 1,
                columns: 1);
            Require(MatrixTileMaccArithmeticAbi.TryCompute(
                    signedInt16,
                    MatrixTileTileImage.Create(1, signedInt16.Left, [0x02, 0x01]),
                    MatrixTileTileImage.Create(2, signedInt16.Right, [0x01, 0x00]),
                    MatrixTileTileImage.Create(3, signedInt16.Accumulator, [0x00, 0x00, 0x00, 0x00]),
                    out MatrixTileTileImage signedInt16Result,
                    out MatrixTileMaccArithmeticFaultKind signedInt16Fault) &&
                signedInt16Fault == MatrixTileMaccArithmeticFaultKind.None &&
                signedInt16Result.Data.SequenceEqual(new byte[] { 0x02, 0x01, 0x00, 0x00 }),
                "SignedInt16ToInt32 did not preserve canonical little-endian exact integer MACC semantics.");
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(signedInt16Result.Data));

            MatrixTileMaccSemanticContract signedInt64 = CreateMaccContract(
                MatrixTileNumericProfileId.SignedInt64ToInt64,
                rows: 1,
                k: 1,
                columns: 1);
            byte[] int64Max = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(int64Max, long.MaxValue);
            byte[] int64One = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(int64One, 1L);
            Require(!MatrixTileMaccArithmeticAbi.TryCompute(
                    signedInt64,
                    MatrixTileTileImage.Create(1, signedInt64.Left, int64One),
                    MatrixTileTileImage.Create(2, signedInt64.Right, int64One),
                    MatrixTileTileImage.Create(3, signedInt64.Accumulator, int64Max),
                    out _,
                    out MatrixTileMaccArithmeticFaultKind overflowFault) &&
                overflowFault == MatrixTileMaccArithmeticFaultKind.ArithmeticOverflow,
                "SignedInt64ToInt64 final accumulator encoding overflow did not fail closed.");
            counters.FailClosedRejectionCount++;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)overflowFault);

            MatrixTileMaccSemanticContract binary32 = CreateMaccContract(
                MatrixTileNumericProfileId.Binary32ToBinary32,
                rows: 1,
                k: 1,
                columns: 1);
            Require(MatrixTileMaccArithmeticAbi.TryCompute(
                    binary32,
                    MatrixTileTileImage.Create(1, binary32.Left, PackUInt32(0x3F800001)),
                    MatrixTileTileImage.Create(2, binary32.Right, PackUInt32(0x3F7FFFFE)),
                    MatrixTileTileImage.Create(3, binary32.Accumulator, PackUInt32(0xBF800000)),
                    out MatrixTileTileImage binary32Result,
                    out MatrixTileMaccArithmeticFaultKind binary32Fault) &&
                binary32Fault == MatrixTileMaccArithmeticFaultKind.None &&
                binary32Result.Data.SequenceEqual(PackUInt32(0x00000000)),
                "Binary32ToBinary32 did not use separate software IEEE multiply/add rounding.");
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(binary32Result.Data));

            MatrixTileMaccSemanticContract binary64 = CreateMaccContract(
                MatrixTileNumericProfileId.Binary64ToBinary64,
                rows: 1,
                k: 1,
                columns: 1);
            Require(MatrixTileMaccArithmeticAbi.TryCompute(
                    binary64,
                    MatrixTileTileImage.Create(1, binary64.Left, PackUInt64(0x3FF8000000000000)),
                    MatrixTileTileImage.Create(2, binary64.Right, PackUInt64(0x4000000000000000)),
                    MatrixTileTileImage.Create(3, binary64.Accumulator, PackUInt64(0x0000000000000000)),
                    out MatrixTileTileImage binary64Result,
                    out MatrixTileMaccArithmeticFaultKind binary64Fault) &&
                binary64Fault == MatrixTileMaccArithmeticFaultKind.None &&
                binary64Result.Data.SequenceEqual(PackUInt64(0x4008000000000000)),
                "Binary64ToBinary64 did not produce the byte-exact software IEEE result.");
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(binary64Result.Data));

            MatrixTileCanonicalDescriptorAbi transposeSource =
                MatrixTileCanonicalDescriptorAbi.Create(2, 3, 1, 3);
            MatrixTileCanonicalDescriptorAbi transposeDestination =
                MatrixTileCanonicalDescriptorAbi.Create(3, 2, 1, 2);
            MatrixTileLayoutPolicy transposeLayout = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();
            MatrixTileTransposeSemanticContract inPlace =
                MatrixTileAccumulatorAndTransposePolicyAbi.CreateTransposeContract(
                    transposeSource,
                    transposeDestination,
                    sourceTileId: 7,
                    destinationTileId: 7,
                    layoutPolicy: transposeLayout);
            Require(MatrixTileAccumulatorAndTransposePolicyAbi.ValidateRuntimeTranspose(inPlace).FaultKind ==
                    MatrixTileSemanticFaultKind.TransposeInPlaceRequiresSquareShape,
                "Non-square in-place transpose did not fail closed at semantic validation.");
            counters.FailClosedRejectionCount++;

            MatrixTileLayoutPolicy tamperedLayout = transposeLayout with
            {
                DestinationAddressing = MatrixTileElementAddressingKind.Blocked,
                Fingerprint = 0
            };
            tamperedLayout = tamperedLayout with
            {
                Fingerprint = MatrixTileLayoutPolicyAbi.ComputeFingerprint(tamperedLayout)
            };
            MatrixTileTransposeSemanticContract invalidLayout =
                MatrixTileAccumulatorAndTransposePolicyAbi.CreateTransposeContract(
                    transposeSource,
                    transposeDestination,
                    sourceTileId: 7,
                    destinationTileId: 8,
                    layoutPolicy: tamperedLayout);
            Require(MatrixTileAccumulatorAndTransposePolicyAbi.ValidateRuntimeTranspose(invalidLayout).FaultKind ==
                    MatrixTileSemanticFaultKind.InvalidLayoutPolicy,
                "Tampered transpose layout did not fail closed at semantic validation.");
            counters.FailClosedRejectionCount++;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, tamperedLayout.Fingerprint);

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileNumericLayoutAbi / formal runtime arithmetic",
                "Signed integer widening/overflow, binary32/binary64 software IEEE arithmetic, and operation-specific transpose layout rejection.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_MACC", "MTRANSPOSE"],
                [
                    "numeric=all supported MatrixTileNumericPolicyAbi profiles byte-exact",
                    "numeric=signed/unsigned integer widening and final little-endian encoding",
                    "numeric=SignedInt64ToInt64 overflow traps before publication",
                    "numeric=Binary32ToBinary32 separate software IEEE rounding",
                    "numeric=Binary64ToBinary64 byte-exact software IEEE result",
                    "layout=Transpose non-square in-place and tampered destination addressing reject"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileNumericLayoutAbi / formal runtime arithmetic", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunGoldenManifestCoveragePressure()
    {
        const string id = "mtile-golden-manifest-coverage-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Require(MatrixTilePositiveGoldenArtifactManifest.HasPositiveExecutableGoldenArtifacts,
                "MatrixTile positive executable golden artifacts are not published by the production manifest.");
            Require(MatrixTilePositiveGoldenArtifactManifest.HasLegalDecodeEncodeRoundTripVectors,
                "MatrixTile golden manifest no longer advertises legal decode/encode round-trip vectors.");
            Require(MatrixTilePositiveGoldenArtifactManifest.HasLegalIrMaterializerProjectionVectors,
                "MatrixTile golden manifest no longer advertises legal IR materializer projection vectors.");
            Require(MatrixTilePositiveGoldenArtifactManifest.HasLegalExecuteRetireVectors,
                "MatrixTile golden manifest no longer advertises execute/retire vectors.");
            Require(MatrixTilePositiveGoldenArtifactManifest.HasMemoryFaultVectors,
                "MatrixTile golden manifest no longer advertises memory fault vectors.");
            Require(MatrixTilePositiveGoldenArtifactManifest.HasDescriptorFaultVectors,
                "MatrixTile golden manifest no longer advertises descriptor fault vectors.");
            Require(MatrixTilePositiveGoldenArtifactManifest.HasNegativeReservedCarrierVectors,
                "MatrixTile golden manifest no longer advertises reserved carrier negative vectors.");
            Require(!MatrixTilePositiveGoldenArtifactManifest.UsesCompilerGeneratedInputs,
                "MatrixTile golden manifest unexpectedly uses compiler-generated inputs as authority.");
            Require(!MatrixTilePositiveGoldenArtifactManifest.UsesFallbackPath,
                "MatrixTile golden manifest unexpectedly allows a fallback path.");
            Require(!MatrixTilePositiveGoldenArtifactManifest.KeepsStatusCatalogOptionalDisabled,
                "MatrixTile golden manifest regressed to optional-disabled status catalog evidence.");
            Require(!MatrixTilePositiveGoldenArtifactManifest.KeepsPositiveCompilerEmissionBlocked,
                "MatrixTile golden manifest still claims positive compiler emission is blocked.");
            Require(MatrixTileNoFallbackEvidenceContract.HasIlCallTargetAudit,
                "MatrixTile no-fallback evidence no longer advertises IL call target audit.");
            Require(MatrixTileNoFallbackEvidenceContract.HasTypedCarrierAudit,
                "MatrixTile no-fallback evidence no longer advertises typed carrier audit.");
            Require(MatrixTileNoFallbackEvidenceContract.HasRuntimeOwnedMemoryAudit,
                "MatrixTile no-fallback evidence no longer advertises runtime-owned memory audit.");
            Require(MatrixTileNoFallbackEvidenceContract.HasCompilerBoundaryAudit,
                "MatrixTile no-fallback evidence no longer advertises compiler boundary audit.");
            Require(!MatrixTileNoFallbackEvidenceContract.UsesFallbackPath,
                "MatrixTile no-fallback evidence unexpectedly allows fallback path.");
            Require(MatrixTileNoFallbackEvidenceContract.AuditedRuntimeTypes.Length >= 8,
                "MatrixTile no-fallback evidence no longer lists the audited runtime type surface.");
            Require(MatrixTileNoFallbackEvidenceContract.ForbiddenCallTargetFragments.Length >= 8,
                "MatrixTile no-fallback evidence no longer lists forbidden fallback call target fragments.");

            ReadOnlySpan<MatrixTileExecutionGoldenVector> execution =
                MatrixTilePositiveGoldenArtifactManifest.ExecutionVectors;
            ReadOnlySpan<MatrixTileMemoryFaultGoldenVector> memoryFaults =
                MatrixTilePositiveGoldenArtifactManifest.MemoryFaultVectors;
            ReadOnlySpan<MatrixTileDescriptorFaultGoldenVector> descriptorFaults =
                MatrixTilePositiveGoldenArtifactManifest.DescriptorFaultVectors;
            ReadOnlySpan<MatrixTileReservedCarrierGoldenVector> reservedCarriers =
                MatrixTilePositiveGoldenArtifactManifest.ReservedCarrierVectors;

            Require(execution.Length == 4, "MatrixTile golden manifest should carry one positive vector per canonical opcode.");
            Require(memoryFaults.Length >= 2, "MatrixTile golden manifest should carry load and store memory fault vectors.");
            Require(descriptorFaults.Length == 4, "MatrixTile golden manifest should carry one descriptor negative per canonical opcode.");
            Require(reservedCarriers.Length == 4, "MatrixTile golden manifest should carry one reserved-carrier negative per canonical opcode.");

            bool hasLoad = false;
            bool hasStore = false;
            bool hasMacc = false;
            bool hasTranspose = false;
            bool hasAccumulator = false;
            bool hasTransposePublication = false;
            bool hasReplayRollback = false;
            foreach (MatrixTileExecutionGoldenVector vector in execution)
            {
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, MixGoldenCarrier(vector.Carrier));
                hasReplayRollback |= !string.IsNullOrWhiteSpace(vector.ExpectedRollbackHex);
                switch (vector.Opcode)
                {
                    case InstructionsEnum.MTILE_LOAD:
                        hasLoad = vector.OperationKind == MatrixTileProjectedOperationKind.Load &&
                            vector.PublicationKind == MatrixTileRetirePublicationKind.TileState;
                        break;
                    case InstructionsEnum.MTILE_STORE:
                        hasStore = vector.OperationKind == MatrixTileProjectedOperationKind.Store &&
                            vector.PublicationKind == MatrixTileRetirePublicationKind.MemoryStore;
                        break;
                    case InstructionsEnum.MTILE_MACC:
                        hasMacc = vector.OperationKind == MatrixTileProjectedOperationKind.Macc &&
                            vector.PublicationKind == MatrixTileRetirePublicationKind.Accumulator;
                        hasAccumulator = true;
                        break;
                    case InstructionsEnum.MTRANSPOSE:
                        hasTranspose = vector.OperationKind == MatrixTileProjectedOperationKind.Transpose &&
                            vector.PublicationKind == MatrixTileRetirePublicationKind.TileState;
                        hasTransposePublication = true;
                        break;
                }
            }

            Require(hasLoad && hasStore && hasMacc && hasTranspose,
                "MatrixTile golden manifest does not cover all four canonical opcodes with matching operation/publication kinds.");
            Require(hasAccumulator && hasTransposePublication && hasReplayRollback,
                "MatrixTile golden manifest no longer covers accumulator, transpose, and rollback expectations.");

            bool hasLoadMemoryFault = false;
            bool hasStoreMemoryFault = false;
            foreach (MatrixTileMemoryFaultGoldenVector vector in memoryFaults)
            {
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, MixGoldenCarrier(vector.Carrier));
                hasLoadMemoryFault |= vector.Opcode == InstructionsEnum.MTILE_LOAD &&
                    vector.ExpectedExecutionFault == MatrixTileExecutionFaultKind.InvalidMemoryShape &&
                    vector.ExpectedRetireFault == MatrixTileRetireFaultKind.CapturedExecutionFault &&
                    vector.ExpectedMemoryFault == MatrixTileMemoryFaultKind.PartialMemoryFault &&
                    vector.ExpectedFaultPoint;
                hasStoreMemoryFault |= vector.Opcode == InstructionsEnum.MTILE_STORE &&
                    vector.ExpectedExecutionFault == MatrixTileExecutionFaultKind.None &&
                    vector.ExpectedRetireFault == MatrixTileRetireFaultKind.MemoryCommitFault;
            }

            Require(hasLoadMemoryFault && hasStoreMemoryFault,
                "MatrixTile golden manifest no longer covers load and store memory fault identities.");

            foreach (MatrixTileDescriptorFaultGoldenVector vector in descriptorFaults)
            {
                Require(vector.ExpectedProjectionFault == MatrixTileIrProjectionFaultKind.InvalidShapeEncoding,
                    $"{vector.Id}: descriptor negative vector no longer fails closed on invalid shape encoding.");
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, MixGoldenCarrier(vector.Carrier));
            }

            foreach (MatrixTileReservedCarrierGoldenVector vector in reservedCarriers)
            {
                Require(vector.ExpectedDecodeDecision.Contains("Reserved", StringComparison.Ordinal),
                    $"{vector.Id}: reserved carrier vector no longer documents reserved decode rejection.");
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, MixGoldenCarrier(vector.Carrier));
            }

            counters.FailClosedRejectionCount = checked((ulong)(memoryFaults.Length + descriptorFaults.Length + reservedCarriers.Length));
            stopwatch.Stop();
            return Success(
                id,
                "MatrixTile golden manifest / runtime-owned corpus coverage",
                "Production positive and negative golden vectors cover four canonical opcodes, retire/replay expectations, memory faults, descriptor faults, and reserved carrier rejection without compiler-generated authority or fallback.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE", "MTILE_MACC", "MTRANSPOSE"],
                [
                    "golden=positive executable vectors for all canonical opcodes",
                    "golden=memory fault vectors for load/store identity",
                    "golden=descriptor and reserved carrier negatives fail closed",
                    "golden=no compiler-generated inputs and no fallback path",
                    "no-fallback=typed carrier, runtime memory, IL call target, and compiler boundary audit advertised"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTile golden manifest / runtime-owned corpus coverage", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunGoldenJsonCorpusLoaderPressure()
    {
        const string id = "mtile-golden-json-corpus-loader-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            string corpusPath = FindRepositoryFile(
                Path.Combine(
                    "Documentation",
                    "Documentation",
                    "Stream WhiteBook",
                    "03_MatrixTile",
                    "Golden",
                    "matrix_tile_numeric_layout_golden_v1.json"));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(corpusPath));
            JsonElement root = document.RootElement;
            Require(root.GetProperty("schemaVersion").GetInt32() == 1,
                "MatrixTile golden JSON schemaVersion drifted from v1.");
            Require(root.GetProperty("numericPolicyAbiVersion").GetInt32() == MatrixTileNumericPolicyAbi.CurrentAbiVersion,
                "MatrixTile golden JSON numeric ABI version drifted from runtime.");
            Require(root.GetProperty("layoutPolicyAbiVersion").GetInt32() == MatrixTileLayoutPolicyAbi.CurrentAbiVersion,
                "MatrixTile golden JSON layout ABI version drifted from runtime.");
            Require(root.GetProperty("corpusDecision").GetString() == "ClosedMachineReadableMatrixTileNumericAndLayoutGoldenCorpus",
                "MatrixTile golden JSON corpus decision drifted.");
            Require(!root.GetProperty("usesCompilerOutput").GetBoolean(),
                "MatrixTile golden JSON unexpectedly uses compiler output as authority.");
            Require(!root.GetProperty("usesPrivateArithmeticOracle").GetBoolean(),
                "MatrixTile golden JSON unexpectedly uses a private arithmetic oracle.");

            JsonElement vectors = root.GetProperty("vectors");
            Require(vectors.ValueKind == JsonValueKind.Array && vectors.GetArrayLength() >= 9,
                "MatrixTile golden JSON no longer carries the expected positive and negative vector mix.");

            int positiveCount = 0;
            int executeFaultCount = 0;
            int projectionFaultCount = 0;
            foreach (JsonElement vector in vectors.EnumerateArray())
            {
                string vectorId = RequiredString(vector, "id");
                string kind = RequiredString(vector, "kind");
                string operation = RequiredString(vector, "operation");
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(System.Text.Encoding.UTF8.GetBytes(vectorId)));

                if (kind == "positive" && operation == "MTILE_MACC")
                {
                    VerifyGoldenJsonMaccVector(vector);
                    positiveCount++;
                    counters.RuntimeInstructionCount++;
                    counters.ResultChecksum = MixChecksum(
                        counters.ResultChecksum,
                        Checksum(HexToBytes(RequiredString(vector, "expectedStagedResultHex"))));
                    continue;
                }

                if (kind == "positive" && operation == "MTRANSPOSE")
                {
                    VerifyGoldenJsonTransposeVector(vector);
                    positiveCount++;
                    counters.RuntimeInstructionCount++;
                    counters.ResultChecksum = MixChecksum(
                        counters.ResultChecksum,
                        Checksum(HexToBytes(RequiredString(vector, "expectedStagedResultHex"))));
                    continue;
                }

                if (kind == "executeFault" && operation == "MTILE_MACC")
                {
                    VerifyGoldenJsonMaccFaultVector(vector);
                    executeFaultCount++;
                    counters.FailClosedRejectionCount++;
                    continue;
                }

                if (kind == "projectionFault")
                {
                    VerifyGoldenJsonProjectionFaultVector(vector);
                    projectionFaultCount++;
                    counters.FailClosedRejectionCount++;
                }
            }

            Require(positiveCount >= 7, "MatrixTile golden JSON positive vector coverage regressed.");
            Require(executeFaultCount >= 1, "MatrixTile golden JSON execute-fault vector coverage regressed.");
            Require(projectionFaultCount >= 2, "MatrixTile golden JSON projection-fault vector coverage regressed.");

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTile WhiteBook golden JSON / production-path loader",
                "Machine-readable MatrixTile golden corpus is loaded read-only, schema/ABI/boundary flags are checked, and positive/fault vectors are replayed against runtime numeric/layout ABI helpers.",
                checked((ulong)vectors.GetArrayLength()),
                counters,
                stopwatch.Elapsed,
                ["MTILE_MACC", "MTRANSPOSE"],
                [
                    "json=schema v1 and runtime ABI version binding",
                    "json=no compiler output and no private arithmetic oracle",
                    "json=positive MACC/transpose vectors validated against runtime ABI",
                    "json=execute/projection fault vectors fail closed"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTile WhiteBook golden JSON / production-path loader", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunMemoryFaultAllOrNonePressure()
    {
        const string id = "mtile-store-memory-fault-all-or-none-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            FailSecondWriteMemory memory = new FailSecondWriteMemory();
            memory.SetLength(0x8000);
            Processor.CPU_Core core = CreateCore(memory);
            MatrixTileMicroOp store = CreateMemoryMicroOp(
                InstructionsEnum.MTILE_STORE,
                StoreDestinationBaseAddress,
                tileId: 90);
            byte[] tileImage = [9, 10, 11, 12];
            byte[] originalMemoryImage = [0x21, 0x22, 0x23, 0x24];

            core.SeedMatrixTileForRuntime(0, 90, store.TileDescriptor, tileImage);
            WriteMemory(memory, StoreDestinationBaseAddress, originalMemoryImage);
            MatrixTileExecutionCaptureRecord capture = ExecuteAndGetCapture(ref core, store);
            Require(!capture.HasFault && capture.StreamTransfer.Direction == MatrixTileStreamTransferDirection.TileEgress,
                "MTILE_STORE fault-pressure scenario did not produce a clean staged egress capture.");
            Require(ReadMemory(memory, StoreDestinationBaseAddress, originalMemoryImage.Length).SequenceEqual(originalMemoryImage),
                "MTILE_STORE mutated memory before retire under fault-pressure setup.");

            ulong invalidationsBefore = core.MatrixTileStreamInvalidationCount;
            memory.Arm();
            MatrixTileRetireOutcome retire = store.RetireCapturedResult(ref core, capture);
            Require(retire.FaultRetired &&
                    retire.RetireFaultKind == MatrixTileRetireFaultKind.MemoryCommitFault &&
                    retire.ExecutionFaultKind == MatrixTileExecutionFaultKind.None &&
                    !retire.CommittedMemory,
                "MTILE_STORE did not retire a deterministic memory commit fault.");
            Require(ReadMemory(memory, StoreDestinationBaseAddress, originalMemoryImage.Length).SequenceEqual(originalMemoryImage),
                "MTILE_STORE memory commit fault left a partial architectural memory image.");
            RequireTile(ref core, 90, store.TileDescriptor, tileImage);
            Require(core.MatrixTileStreamInvalidationCount > invalidationsBefore,
                "MTILE_STORE memory commit fault did not invalidate overlapping MatrixTile SRF windows.");
            counters.FailClosedRejectionCount++;
            counters.StreamInvalidationCount += core.MatrixTileStreamInvalidationCount - invalidationsBefore;

            MatrixTileReplayRollbackJournal journal = RequireJournal(store);
            MatrixTileRollbackOutcome rollback = store.RollbackRetiredResult(ref core, journal.ReplayIdentity);
            Require(rollback.FaultOnlyRollback,
                "MTILE_STORE memory fault rollback was not recorded as a fault-only rollback.");
            MatrixTileRetireOutcome replay = store.ReplayRolledBackResult(ref core, journal.ReplayIdentity);
            Require(replay.Equals(retire),
                "MTILE_STORE memory fault replay did not reproduce the deterministic retire fault outcome.");
            Require(ReadMemory(memory, StoreDestinationBaseAddress, originalMemoryImage.Length).SequenceEqual(originalMemoryImage),
                "MTILE_STORE memory fault replay changed architectural memory.");

            counters.RuntimeInstructionCount++;
            counters.ReplayRoundTripCount++;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)retire.PublicationKind);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)retire.RetireFaultKind);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(originalMemoryImage));
            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileMemory / retire fault all-or-none",
                "A staged MTILE_STORE whose second row write fails at retire must roll back the first row and replay the same fault without partial memory visibility.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_STORE"],
                [
                    "execute capture remains side-effect-free",
                    "retire reports MemoryCommitFault",
                    "all-or-none rollback preserves original memory",
                    "fault-only rollback/replay preserves deterministic fault identity"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileMemory / retire fault all-or-none", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunLoadMemoryFaultNoPublicationPressure()
    {
        const string id = "mtile-load-memory-fault-no-publication-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Processor.MainMemoryArea memory = new Processor.MainMemoryArea();
            memory.SetLength(0x400);
            Processor.CPU_Core core = CreateCore(memory);
            MatrixTileMicroOp load = CreateMemoryMicroOp(
                InstructionsEnum.MTILE_LOAD,
                memoryAddress: 0x3FF,
                tileId: 91);
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 91, out _),
                "MTILE_LOAD destination tile was visible before the faulting load.");

            MatrixTileExecutionCaptureRecord capture = ExecuteAndGetCapture(ref core, load);
            Require(capture.HasFault &&
                    capture.FaultKind == MatrixTileExecutionFaultKind.InvalidMemoryShape &&
                    capture.MemoryFaultKind == MatrixTileMemoryFaultKind.PartialMemoryFault &&
                    capture.HasFaultPoint &&
                    capture.FaultPoint.Row == 0 &&
                    capture.FaultPoint.Address == 0x3FF &&
                    capture.FaultPoint.IsStore == false,
                "MTILE_LOAD did not capture the expected partial-row memory fault identity.");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 91, out _),
                "Faulting MTILE_LOAD execute capture published partial tile state.");

            MatrixTileRetireOutcome retire = load.RetireCapturedResult(ref core, capture);
            Require(retire.FaultRetired &&
                    retire.RetireFaultKind == MatrixTileRetireFaultKind.CapturedExecutionFault &&
                    retire.ExecutionFaultKind == MatrixTileExecutionFaultKind.InvalidMemoryShape &&
                    retire.MemoryFaultKind == MatrixTileMemoryFaultKind.PartialMemoryFault &&
                    !retire.PublishedArchitecturalState &&
                    !retire.CommittedMemory,
                "MTILE_LOAD partial-row fault did not retire as a deterministic captured execution fault.");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 91, out _),
                "Fault-retired MTILE_LOAD published partial tile state.");

            MatrixTileReplayRollbackJournal journal = RequireJournal(load);
            MatrixTileRollbackOutcome rollback = load.RollbackRetiredResult(ref core, journal.ReplayIdentity);
            Require(rollback.FaultOnlyRollback,
                "MTILE_LOAD partial-row fault rollback was not recorded as a fault-only rollback.");
            MatrixTileRetireOutcome replay = load.ReplayRolledBackResult(ref core, journal.ReplayIdentity);
            Require(replay.Equals(retire),
                "MTILE_LOAD partial-row fault replay did not reproduce the deterministic retire fault outcome.");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 91, out _),
                "MTILE_LOAD partial-row fault replay published partial tile state.");

            counters.RuntimeInstructionCount++;
            counters.FailClosedRejectionCount++;
            counters.ReplayRoundTripCount++;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)capture.FaultKind);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)capture.MemoryFaultKind);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, capture.FaultPoint.Address);
            stopwatch.Stop();
            return Success(
                id,
                "MatrixTileMemory / load partial-row fault no-publication",
                "A faulting MTILE_LOAD captures a precise partial-row memory fault, retires fault-only, and never publishes partial tile state across rollback/replay.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD"],
                [
                    "execute captures PartialMemoryFault with precise row/address",
                    "retire reports CapturedExecutionFault",
                    "no partial tile publication before or after retire",
                    "fault-only rollback/replay preserves deterministic fault identity"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTileMemory / load partial-row fault no-publication", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunCompilerSidebandLoweringConformance()
    {
        const string id = "mtile-compiler-sideband-lowering-conformance";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            MatrixTileCanonicalDescriptorAbi runtimeDescriptor = MatrixTileCanonicalDescriptorAbi.Create(
                rows: MatrixSide,
                columns: MatrixSide,
                elementSizeBytes: 1,
                strideBytes: MatrixSide);
            CompilerMatrixTileDescriptorAbi descriptor = CreateCompilerDescriptor(runtimeDescriptor, DataTypeEnum.INT8);
            MatrixTileNumericPolicy numericPolicy =
                CreateRuntimeNumericPolicy(DataTypeEnum.INT8);
            MatrixTileLayoutPolicy maccLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateMaccPolicy();
            MatrixTileLayoutPolicy transposeLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicy =
                CreateCompilerAccumulatorPolicy(
                    runtimeDescriptor,
                    numericPolicy,
                    maccLayoutPolicy);
            CompilerMatrixTileTransposePolicyAbi transposePolicy =
                CreateCompilerTransposePolicy(runtimeDescriptor, transposeLayoutPolicy);
            HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

            context.CompileMtileLoadWithDecision(
                CompilerMatrixTileTileOperand.Create(1),
                descriptor,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress));
            context.CompileMtileStoreWithDecision(
                CompilerMatrixTileTileOperand.Create(1),
                descriptor,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(StoreDestinationBaseAddress));
            context.CompileMtileMaccWithDecision(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                CompilerMatrixTileTileOperand.Create(3),
                descriptor,
                accumulatorPolicy);
            context.CompileMtransposeWithDecision(
                CompilerMatrixTileTileOperand.Create(3),
                CompilerMatrixTileTileOperand.Create(4),
                descriptor,
                transposePolicy);

            AssertSourceSidebands(context, 0, InstructionsEnum.MTILE_LOAD, null, null);
            AssertSourceSidebands(context, 1, InstructionsEnum.MTILE_STORE, null, null);
            AssertSourceSidebands(context, 2, InstructionsEnum.MTILE_MACC, numericPolicy, maccLayoutPolicy);
            AssertSourceSidebands(context, 3, InstructionsEnum.MTRANSPOSE, null, transposeLayoutPolicy);

            HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTILE_LOAD, null, null, requireLane6: true);
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTILE_STORE, null, null, requireLane6: true);
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTILE_MACC, numericPolicy, maccLayoutPolicy, requireLane6: false);
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTRANSPOSE, null, transposeLayoutPolicy, requireLane6: false);

            counters.CompilerEmissionCount = 4;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)compiledProgram.BundleCount);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, numericPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, maccLayoutPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, transposeLayoutPolicy.Fingerprint);
            stopwatch.Stop();
            return Success(
                id,
                "Compiler transport conformance",
                "One typed emission of every canonical MatrixTile opcode; compiler-owned policies must cross the explicit runtime transport seam without semantic drift.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE", "MTILE_MACC", "MTRANSPOSE"],
                [
                    "MTILE_LOAD/STORE carry no compute numeric/layout sideband",
                    "MTILE_MACC preserves explicit numeric and layout sidebands in source and lowered InstructionSlotMetadata",
                    "MTRANSPOSE preserves layout-only sideband in source and lowered InstructionSlotMetadata",
                    "lowered MatrixTile memory transport is physically placed on lane6"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Compiler transport conformance", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunCompilerLoweredRuntimeExecutionPressure()
    {
        const string id = "mtile-compiler-lowered-runtime-execution-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            MatrixTileCanonicalDescriptorAbi runtimeDescriptor = MatrixTileCanonicalDescriptorAbi.Create(
                rows: MatrixSide,
                columns: MatrixSide,
                elementSizeBytes: 1,
                strideBytes: MatrixSide);
            CompilerMatrixTileDescriptorAbi descriptor = CreateCompilerDescriptor(runtimeDescriptor, DataTypeEnum.INT8);
            MatrixTileNumericPolicy numericPolicy =
                CreateRuntimeNumericPolicy(DataTypeEnum.INT8);
            MatrixTileLayoutPolicy maccLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateMaccPolicy();
            MatrixTileLayoutPolicy transposeLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicy =
                CreateCompilerAccumulatorPolicy(
                    runtimeDescriptor,
                    numericPolicy,
                    maccLayoutPolicy);
            CompilerMatrixTileTransposePolicyAbi transposePolicy =
                CreateCompilerTransposePolicy(runtimeDescriptor, transposeLayoutPolicy);
            HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

            context.CompileMtileLoadWithDecision(
                CompilerMatrixTileTileOperand.Create(101),
                descriptor,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress));
            context.CompileMtileStoreWithDecision(
                CompilerMatrixTileTileOperand.Create(101),
                descriptor,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(StoreDestinationBaseAddress));
            context.CompileMtileMaccWithDecision(
                CompilerMatrixTileTileOperand.Create(101),
                CompilerMatrixTileTileOperand.Create(102),
                CompilerMatrixTileTileOperand.Create(103),
                descriptor,
                accumulatorPolicy);
            context.CompileMtransposeWithDecision(
                CompilerMatrixTileTileOperand.Create(101),
                CompilerMatrixTileTileOperand.Create(104),
                descriptor,
                transposePolicy);

            HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
            MatrixTileMicroOp loweredLoad = MaterializeLoweredMatrixTileMicroOp(
                compiledProgram,
                InstructionsEnum.MTILE_LOAD);
            MatrixTileMicroOp loweredStore = MaterializeLoweredMatrixTileMicroOp(
                compiledProgram,
                InstructionsEnum.MTILE_STORE);
            MatrixTileMicroOp loweredMacc = MaterializeLoweredMatrixTileMicroOp(
                compiledProgram,
                InstructionsEnum.MTILE_MACC);
            MatrixTileMicroOp loweredTranspose = MaterializeLoweredMatrixTileMicroOp(
                compiledProgram,
                InstructionsEnum.MTRANSPOSE);

            Processor.MainMemoryArea memory = CreateMemory(1);
            Processor.CPU_Core core = CreateCore(memory);
            byte[] loadPayload = [1, 2, 3, 4];
            WriteMemory(memory, LoadSourceBaseAddress, loadPayload);

            MatrixTileExecutionCaptureRecord loadCapture = ExecuteAndGetCapture(ref core, loweredLoad);
            RetireAndReplay(ref core, loweredLoad, loadCapture, loadPayload, 101, loweredLoad.ResultTileDescriptor, ref counters);
            RequireTile(ref core, 101, loweredLoad.ResultTileDescriptor, loadPayload);

            MatrixTileExecutionCaptureRecord storeCapture = ExecuteAndGetCapture(ref core, loweredStore);
            MatrixTileRetireOutcome storeRetire = loweredStore.RetireCapturedResult(ref core, storeCapture);
            Require(storeRetire.IsSuccess && storeRetire.CommittedMemory,
                "Compiler-lowered MTILE_STORE did not retire through runtime memory publication.");
            Require(ReadMemory(memory, StoreDestinationBaseAddress, loadPayload.Length).SequenceEqual(loadPayload),
                "Compiler-lowered MTILE_STORE did not publish the loaded tile image.");
            counters.RetirePublicationCount++;
            MatrixTileReplayRollbackJournal storeJournal = RequireJournal(loweredStore);
            MatrixTileRollbackOutcome storeRollback = loweredStore.RollbackRetiredResult(ref core, storeJournal.ReplayIdentity);
            Require(storeRollback.RestoredMemory,
                "Compiler-lowered MTILE_STORE rollback did not restore the memory checkpoint.");
            MatrixTileRetireOutcome storeReplay = loweredStore.ReplayRolledBackResult(ref core, storeJournal.ReplayIdentity);
            Require(storeReplay.IsSuccess && storeReplay.CommittedMemory,
                "Compiler-lowered MTILE_STORE replay did not restore the runtime memory publication.");
            counters.ReplayRoundTripCount++;
            counters.StreamBytesTransferred += checked((ulong)(loadPayload.Length * 2));

            core.SeedMatrixTileForRuntime(0, 102, loweredMacc.SecondaryTileDescriptor, [1, 0, 0, 1]);
            byte[] accumulatorBefore = new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(loweredMacc.ResultTileDescriptor)];
            core.SeedMatrixTileForRuntime(0, 103, loweredMacc.ResultTileDescriptor, accumulatorBefore);
            MatrixTileExecutionCaptureRecord maccCapture = ExecuteAndGetCapture(ref core, loweredMacc);
            byte[] expectedMacc = PackInt32(1, 2, 3, 4);
            RetireAndReplay(ref core, loweredMacc, maccCapture, expectedMacc, 103, loweredMacc.ResultTileDescriptor, ref counters);

            MatrixTileExecutionCaptureRecord transposeCapture = ExecuteAndGetCapture(ref core, loweredTranspose);
            byte[] expectedTranspose = [1, 3, 2, 4];
            RetireAndReplay(ref core, loweredTranspose, transposeCapture, expectedTranspose, 104, loweredTranspose.ResultTileDescriptor, ref counters);

            counters.CompilerEmissionCount = 4;
            counters.RuntimeInstructionCount = 4;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(loadPayload));
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(expectedMacc));
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(expectedTranspose));
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)compiledProgram.BundleCount);
            stopwatch.Stop();
            return Success(
                id,
                "Compiler lowered bundle / runtime carrier execution",
                "Compiler-positive MatrixTile emissions are lowered, decoded through canonical bundle transport, materialized as MatrixTileMicroOp carriers, and executed through runtime capture/retire/replay.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE", "MTILE_MACC", "MTRANSPOSE"],
                [
                    "lowered MTILE_LOAD materializes as runtime MatrixTileMicroOp and retires tile state",
                    "lowered MTILE_STORE materializes as runtime MatrixTileMicroOp and commits memory at retire",
                    "lowered MTILE_MACC executes runtime-owned numeric/layout arithmetic",
                    "lowered MTRANSPOSE executes runtime-owned layout permutation"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Compiler lowered bundle / runtime carrier execution", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunFullPipelineE2ePressure()
    {
        const string id = "mtile-full-pipeline-e2e-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            MatrixTileCanonicalDescriptorAbi runtimeDescriptor = MatrixTileCanonicalDescriptorAbi.Create(
                rows: MatrixSide,
                columns: MatrixSide,
                elementSizeBytes: 1,
                strideBytes: MatrixSide);
            CompilerMatrixTileDescriptorAbi descriptor = CreateCompilerDescriptor(runtimeDescriptor, DataTypeEnum.INT8);
            MatrixTileNumericPolicy numericPolicy =
                CreateRuntimeNumericPolicy(DataTypeEnum.INT8);
            MatrixTileLayoutPolicy maccLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateMaccPolicy();
            MatrixTileLayoutPolicy transposeLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicy =
                CreateCompilerAccumulatorPolicy(
                    runtimeDescriptor,
                    numericPolicy,
                    maccLayoutPolicy);
            CompilerMatrixTileTransposePolicyAbi transposePolicy =
                CreateCompilerTransposePolicy(runtimeDescriptor, transposeLayoutPolicy);

            HybridCpuCompiledProgram compiledProgram = BuildCompilerProgram(context =>
            {
                context.CompileMtileLoadWithDecision(
                    CompilerMatrixTileTileOperand.Create(21),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress));
                context.CompileMtileStoreWithDecision(
                    CompilerMatrixTileTileOperand.Create(22),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(StoreDestinationBaseAddress));
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(23),
                    CompilerMatrixTileTileOperand.Create(24),
                    CompilerMatrixTileTileOperand.Create(25),
                    descriptor,
                    accumulatorPolicy);
                context.CompileMtransposeWithDecision(
                    CompilerMatrixTileTileOperand.Create(26),
                    CompilerMatrixTileTileOperand.Create(27),
                    descriptor,
                    transposePolicy);
            });

            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTILE_LOAD, null, null, requireLane6: true);
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTILE_STORE, null, null, requireLane6: true);
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTILE_MACC, numericPolicy, maccLayoutPolicy, requireLane6: false);
            AssertLoweredSidebands(compiledProgram, InstructionsEnum.MTRANSPOSE, null, transposeLayoutPolicy, requireLane6: false);

            Processor.MainMemoryArea memory = CreateMemory(1);
            Processor.CPU_Core core = CreateCore(memory);
            byte[] loadPayload = [1, 2, 3, 4];
            MatrixTileCanonicalDescriptorAbi runtimeRightDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(accumulatorPolicy.RightSourceDescriptor);
            MatrixTileCanonicalDescriptorAbi runtimeAccumulatorDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(accumulatorPolicy.AccumulatorDescriptor);
            MatrixTileCanonicalDescriptorAbi runtimeTransposeDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(transposePolicy.DestinationDescriptor);
            byte[] accumulatorBefore = new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(runtimeAccumulatorDescriptor)];
            byte[] expectedMacc = PackInt32(1, 2, 3, 4);
            byte[] expectedTranspose = [1, 3, 2, 4];
            WriteMemory(memory, LoadSourceBaseAddress, loadPayload);

            MatrixTileFullPipelineReport pipelineReport =
                MatrixTileFullPipelineHarness.RunCompilerLoweredBundlesForTesting(
                    ref core,
                    NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundles),
                    NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundleAnnotations),
                    new MatrixTileFullPipelineHarnessOptions
                    {
                        BeforeExecute = (ref Processor.CPU_Core callbackCore, MatrixTileFullPipelineStepEvidence step) =>
                        {
                            if (step.Opcode == InstructionsEnum.MTILE_STORE)
                            {
                                Require(step.DependencyMetadata.SourceTileId == 22,
                                    "Full pipeline store decoded tile dependency identifier drifted from compiler emission.");
                                callbackCore.SeedMatrixTileForRuntime(
                                    0,
                                    step.DependencyMetadata.SourceTileId,
                                    runtimeDescriptor,
                                    loadPayload);
                            }
                            else if (step.Opcode == InstructionsEnum.MTILE_MACC)
                            {
                                Require(step.DependencyMetadata.SourceTileId == 23 &&
                                        step.DependencyMetadata.SecondaryTileId == 24 &&
                                        step.DependencyMetadata.DestinationTileId == 25,
                                    "Full pipeline MACC decoded tile dependency identifiers drifted from compiler emission.");
                                callbackCore.SeedMatrixTileForRuntime(
                                    0,
                                    step.DependencyMetadata.SourceTileId,
                                    runtimeDescriptor,
                                    loadPayload);
                                callbackCore.SeedMatrixTileForRuntime(
                                    0,
                                    step.DependencyMetadata.SecondaryTileId,
                                    runtimeRightDescriptor,
                                    [1, 0, 0, 1]);
                                callbackCore.SeedMatrixTileForRuntime(
                                    0,
                                    step.DependencyMetadata.DestinationTileId,
                                    runtimeAccumulatorDescriptor,
                                    accumulatorBefore);
                                RequireTile(
                                    ref callbackCore,
                                    step.DependencyMetadata.SecondaryTileId,
                                    runtimeRightDescriptor,
                                    [1, 0, 0, 1]);
                                RequireTile(
                                    ref callbackCore,
                                    step.DependencyMetadata.DestinationTileId,
                                    runtimeAccumulatorDescriptor,
                                    accumulatorBefore);
                            }
                            else if (step.Opcode == InstructionsEnum.MTRANSPOSE)
                            {
                                Require(step.DependencyMetadata.SourceTileId == 26 &&
                                        step.DependencyMetadata.DestinationTileId == 27,
                                    "Full pipeline transpose decoded tile dependency identifiers drifted from compiler emission.");
                                callbackCore.SeedMatrixTileForRuntime(
                                    0,
                                    step.DependencyMetadata.SourceTileId,
                                    runtimeDescriptor,
                                    loadPayload);
                            }
                        },
                        BeforeRetire = (ref Processor.CPU_Core callbackCore, MatrixTileFullPipelineStepEvidence step, MatrixTileExecutionCaptureRecord unusedCapture) =>
                        {
                            switch (step.Opcode)
                            {
                                case InstructionsEnum.MTILE_LOAD:
                                    Require(!callbackCore.TryCaptureAnyMatrixTileSnapshot(0, 21, out _),
                                        "Full pipeline MTILE_LOAD published tile state before retire.");
                                    break;
                                case InstructionsEnum.MTILE_STORE:
                                    Require(ReadMemory(memory, StoreDestinationBaseAddress, loadPayload.Length).All(static value => value == 0),
                                        "Full pipeline MTILE_STORE committed memory before retire.");
                                    break;
                                case InstructionsEnum.MTILE_MACC:
                                    RequireTile(ref callbackCore, 25, runtimeAccumulatorDescriptor, accumulatorBefore);
                                    break;
                                case InstructionsEnum.MTRANSPOSE:
                                    Require(!callbackCore.TryCaptureAnyMatrixTileSnapshot(0, 27, out _),
                                        "Full pipeline MTRANSPOSE published destination tile before retire.");
                                    break;
                            }
                        },
                        AfterRetire = (ref Processor.CPU_Core callbackCore, MatrixTileFullPipelineStepEvidence step, MatrixTileExecutionCaptureRecord unusedCapture, MatrixTileRetireOutcome outcome) =>
                        {
                            Require(outcome.IsSuccess, $"Full pipeline {step.Opcode} did not retire successfully: {outcome.Message}");
                            switch (step.Opcode)
                            {
                                case InstructionsEnum.MTILE_LOAD:
                                    RequireTile(ref callbackCore, 21, runtimeDescriptor, loadPayload);
                                    break;
                                case InstructionsEnum.MTILE_STORE:
                                    Require(ReadMemory(memory, StoreDestinationBaseAddress, loadPayload.Length).SequenceEqual(loadPayload),
                                        "Full pipeline MTILE_STORE retire did not commit the loaded tile image.");
                                    break;
                                case InstructionsEnum.MTILE_MACC:
                                    RequireTile(ref callbackCore, 25, runtimeAccumulatorDescriptor, expectedMacc);
                                    break;
                                case InstructionsEnum.MTRANSPOSE:
                                    RequireTile(ref callbackCore, 27, runtimeTransposeDescriptor, expectedTranspose);
                                    break;
                            }
                        }
                    });

            Require(pipelineReport.Steps.Count == 4,
                "Full pipeline E2E did not observe all four compiler-emitted MatrixTile opcodes.");
            foreach (MatrixTileFullPipelineStepEvidence step in pipelineReport.Steps)
            {
                Require(!step.FailClosedRejected, $"Positive full pipeline step rejected unexpectedly: {step.Opcode}: {step.FailureMessage}");
                Require(step.FetchObserved && step.DecodeObserved && step.ScheduleObserved && step.ExecuteObserved && step.RetireObserved,
                    $"{step.Opcode} did not traverse fetch/decode/schedule/execute/retire.");
                Require(step.ReplayRollbackObserved,
                    $"{step.Opcode} did not complete replay/rollback validation.");
                Require(step.SidebandPreserved,
                    $"{step.Opcode} lost source-to-decoded InstructionSlotMetadata sidebands.");
            }

            MatrixTileFullPipelineStepEvidence loadStep = RequirePipelineStep(pipelineReport, InstructionsEnum.MTILE_LOAD);
            MatrixTileFullPipelineStepEvidence storeStep = RequirePipelineStep(pipelineReport, InstructionsEnum.MTILE_STORE);
            MatrixTileFullPipelineStepEvidence maccStep = RequirePipelineStep(pipelineReport, InstructionsEnum.MTILE_MACC);
            MatrixTileFullPipelineStepEvidence transposeStep = RequirePipelineStep(pipelineReport, InstructionsEnum.MTRANSPOSE);
            Require(loadStep.ScheduledLaneIndex == MatrixTileResourceContour.TileStreamLaneId &&
                    storeStep.ScheduledLaneIndex == MatrixTileResourceContour.TileStreamLaneId &&
                    loadStep.RequiredSlotClass == SlotClass.MatrixTileStreamClass &&
                    storeStep.RequiredSlotClass == SlotClass.MatrixTileStreamClass,
                "Full pipeline MatrixTile memory transport was not scheduled on lane6/MatrixTileStreamClass.");
            Require(maccStep.RequiredSlotClass == SlotClass.AluClass &&
                    transposeStep.RequiredSlotClass == SlotClass.AluClass,
                "Full pipeline MatrixTile compute transport did not remain on the compute slot contour.");
            Require(!loadStep.Capture!.Value.NumericPolicy.HasValue &&
                    !loadStep.Capture.Value.LayoutPolicy.HasValue &&
                    !storeStep.Capture!.Value.NumericPolicy.HasValue &&
                    !storeStep.Capture.Value.LayoutPolicy.HasValue,
                "Full pipeline memory captures acquired compute numeric/layout authority.");
            Require(Nullable.Equals(maccStep.Capture!.Value.NumericPolicy, numericPolicy) &&
                    Nullable.Equals(maccStep.Capture.Value.LayoutPolicy, maccLayoutPolicy),
                "Full pipeline MACC capture did not carry runtime-owned numeric/layout policy identity.");
            Require(!transposeStep.Capture!.Value.NumericPolicy.HasValue &&
                    Nullable.Equals(transposeStep.Capture.Value.LayoutPolicy, transposeLayoutPolicy),
                "Full pipeline transpose capture did not preserve layout-only policy identity.");
            RequireTile(ref core, 21, runtimeDescriptor, loadPayload);
            RequireTile(ref core, 25, runtimeAccumulatorDescriptor, expectedMacc);
            RequireTile(ref core, 27, runtimeTransposeDescriptor, expectedTranspose);
            Require(ReadMemory(memory, StoreDestinationBaseAddress, loadPayload.Length).SequenceEqual(loadPayload),
                "Full pipeline store replay did not leave the committed image restored.");

            ulong failClosedRejections = 0;
            int negativeCompilerEmissions = 0;

            HybridCpuCompiledProgram missingMaccNumeric = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(301),
                    CompilerMatrixTileTileOperand.Create(302),
                    CompilerMatrixTileTileOperand.Create(303),
                    descriptor,
                    accumulatorPolicy));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                missingMaccNumeric,
                MutateLoweredMatrixTileAnnotation(
                    missingMaccNumeric,
                    InstructionsEnum.MTILE_MACC,
                    metadata => metadata with { MatrixTileNumericPolicy = null }),
                "missing MACC numeric sideband");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram tamperedMaccNumeric = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(311),
                    CompilerMatrixTileTileOperand.Create(312),
                    CompilerMatrixTileTileOperand.Create(313),
                    descriptor,
                    accumulatorPolicy));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                tamperedMaccNumeric,
                MutateLoweredMatrixTileAnnotation(
                    tamperedMaccNumeric,
                    InstructionsEnum.MTILE_MACC,
                    metadata => metadata with
                    {
                        MatrixTileNumericPolicy = metadata.MatrixTileNumericPolicy!.Value with
                        {
                            Fingerprint = metadata.MatrixTileNumericPolicy.Value.Fingerprint ^ 0x55UL
                        }
                    }),
                "tampered MACC numeric fingerprint");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram mismatchedMaccLayout = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(321),
                    CompilerMatrixTileTileOperand.Create(322),
                    CompilerMatrixTileTileOperand.Create(323),
                    descriptor,
                    accumulatorPolicy));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                mismatchedMaccLayout,
                MutateLoweredMatrixTileAnnotation(
                    mismatchedMaccLayout,
                    InstructionsEnum.MTILE_MACC,
                    metadata => metadata with { MatrixTileLayoutPolicy = transposeLayoutPolicy }),
                "operation-mismatched MACC layout sideband");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram transposeWithNumeric = BuildCompilerProgram(context =>
                context.CompileMtransposeWithDecision(
                    CompilerMatrixTileTileOperand.Create(331),
                    CompilerMatrixTileTileOperand.Create(332),
                    descriptor,
                    transposePolicy));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                transposeWithNumeric,
                MutateLoweredMatrixTileAnnotation(
                    transposeWithNumeric,
                    InstructionsEnum.MTRANSPOSE,
                    metadata => metadata with { MatrixTileNumericPolicy = numericPolicy }),
                "MTRANSPOSE with MACC numeric sideband");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram transposeWrongLayout = BuildCompilerProgram(context =>
                context.CompileMtransposeWithDecision(
                    CompilerMatrixTileTileOperand.Create(341),
                    CompilerMatrixTileTileOperand.Create(342),
                    descriptor,
                    transposePolicy));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                transposeWrongLayout,
                MutateLoweredMatrixTileAnnotation(
                    transposeWrongLayout,
                    InstructionsEnum.MTRANSPOSE,
                    metadata => metadata with { MatrixTileLayoutPolicy = maccLayoutPolicy }),
                "MTRANSPOSE with MACC layout sideband");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram loadWithComputeSideband = BuildCompilerProgram(context =>
                context.CompileMtileLoadWithDecision(
                    CompilerMatrixTileTileOperand.Create(351),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress)));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                loadWithComputeSideband,
                MutateLoweredMatrixTileAnnotation(
                    loadWithComputeSideband,
                    InstructionsEnum.MTILE_LOAD,
                    metadata => metadata with
                    {
                        MatrixTileNumericPolicy = numericPolicy,
                        MatrixTileLayoutPolicy = maccLayoutPolicy
                    }),
                "MTILE_LOAD with compute sideband authority");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram storeWithComputeSideband = BuildCompilerProgram(context =>
                context.CompileMtileStoreWithDecision(
                    CompilerMatrixTileTileOperand.Create(361),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(StoreDestinationBaseAddress)));
            failClosedRejections += ExpectFullPipelineProjectionRejection(
                storeWithComputeSideband,
                MutateLoweredMatrixTileAnnotation(
                    storeWithComputeSideband,
                    InstructionsEnum.MTILE_STORE,
                    metadata => metadata with
                    {
                        MatrixTileNumericPolicy = numericPolicy,
                        MatrixTileLayoutPolicy = maccLayoutPolicy
                    }),
                "MTILE_STORE with compute sideband authority");
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram loadWrongResource = BuildCompilerProgram(context =>
                context.CompileMtileLoadWithDecision(
                    CompilerMatrixTileTileOperand.Create(371),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress)));
            failClosedRejections += ExpectFullPipelineRetireRejection(
                loadWrongResource,
                capture => capture with
                {
                    StreamTransfer = capture.StreamTransfer with
                    {
                        ResourceClass = MatrixTileRuntimeResourceClass.MatrixTileCompute
                    }
                },
                "wrong lane/resource class for MatrixTile memory transport");
            negativeCompilerEmissions++;

            counters.CompilerEmissionCount = checked((ulong)(4 + negativeCompilerEmissions));
            counters.RuntimeInstructionCount = checked((ulong)pipelineReport.RuntimeInstructionCount + 1UL);
            counters.RetirePublicationCount = checked((ulong)pipelineReport.RetirePublicationCount);
            counters.ReplayRoundTripCount = checked((ulong)pipelineReport.ReplayRoundTripCount);
            counters.FailClosedRejectionCount = failClosedRejections;
            counters.StreamBytesTransferred = checked((ulong)(loadPayload.Length * 2));
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)compiledProgram.BundleCount);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, numericPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, maccLayoutPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, transposeLayoutPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, failClosedRejections);
            stopwatch.Stop();
            return Success(
                id,
                "Full pipeline E2E / compiler lowered MatrixTile fetch-decode-schedule-retire",
                "Compiler-positive MatrixTile program traverses lowered bundles, lowered InstructionSlotMetadata, fetch/decode, scheduler placement, execute capture, retire publication, replay/rollback, and fail-closed E2E negatives.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE", "MTILE_MACC", "MTRANSPOSE"],
                [
                    "compiler-emissions=positive canonical four-op program plus targeted negative compiler emissions",
                    "fetch/decode=VliwDecoderV4 with lowered VliwBundleAnnotations",
                    "schedule=MicroOpScheduler.PackBundleIntraCoreSmt carrier placement",
                    "lane6=MTILE_LOAD/STORE scheduled as MatrixTileStreamClass on lane6",
                    "retire-only=load tile, store memory, MACC accumulator, transpose destination tile",
                    "replay=all four positive operations rollback and replay through retire-owned journal",
                    "sideband-preservation=source and decoded InstructionSlotMetadata policy identities match",
                    "fail-closed=missing/tampered/mismatched sidebands and wrong memory resource identity reject before publication"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Full pipeline E2E / compiler lowered MatrixTile fetch-decode-schedule-retire", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunProductionStageFlowE2ePressure()
    {
        const string id = "mtile-production-stageflow-e2e-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            MatrixTileCanonicalDescriptorAbi descriptor = MatrixTileCanonicalDescriptorAbi.Create(
                rows: MatrixSide,
                columns: MatrixSide,
                elementSizeBytes: 1,
                strideBytes: MatrixSide);
            CompilerMatrixTileDescriptorAbi compilerDescriptor = CreateCompilerDescriptor(descriptor, DataTypeEnum.INT8);
            MatrixTileNumericPolicy numericPolicy =
                CreateRuntimeNumericPolicy(DataTypeEnum.INT8);
            MatrixTileLayoutPolicy maccLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateMaccPolicy();
            MatrixTileLayoutPolicy transposeLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicy =
                CreateCompilerAccumulatorPolicy(
                    descriptor,
                    numericPolicy,
                    maccLayoutPolicy);
            CompilerMatrixTileTransposePolicyAbi transposePolicy =
                CreateCompilerTransposePolicy(descriptor, transposeLayoutPolicy);

            HybridCpuCompiledProgram loadProgram = BuildCompilerProgram(context =>
                context.CompileMtileLoadWithDecision(
                    CompilerMatrixTileTileOperand.Create(41),
                    compilerDescriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress)));
            HybridCpuCompiledProgram storeProgram = BuildCompilerProgram(context =>
                context.CompileMtileStoreWithDecision(
                    CompilerMatrixTileTileOperand.Create(41),
                    compilerDescriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(StoreDestinationBaseAddress)));
            HybridCpuCompiledProgram maccProgram = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(41),
                    CompilerMatrixTileTileOperand.Create(42),
                    CompilerMatrixTileTileOperand.Create(43),
                    compilerDescriptor,
                    accumulatorPolicy));
            HybridCpuCompiledProgram transposeProgram = BuildCompilerProgram(context =>
                context.CompileMtransposeWithDecision(
                    CompilerMatrixTileTileOperand.Create(41),
                    CompilerMatrixTileTileOperand.Create(44),
                    compilerDescriptor,
                    transposePolicy));

            Processor.MainMemoryArea memory = CreateMemory(1);
            Processor.CPU_Core core = CreateCore(memory);
            byte[] source = [1, 2, 3, 4];
            MatrixTileCanonicalDescriptorAbi runtimeRightDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(accumulatorPolicy.RightSourceDescriptor);
            MatrixTileCanonicalDescriptorAbi runtimeAccumulatorDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(accumulatorPolicy.AccumulatorDescriptor);
            MatrixTileCanonicalDescriptorAbi runtimeTransposeDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(transposePolicy.DestinationDescriptor);
            byte[] accumulatorBefore = new byte[
                MatrixTileExecuteCaptureAbi.GetPackedByteLength(runtimeAccumulatorDescriptor)];
            byte[] expectedMacc = PackInt32(1, 2, 3, 4);
            byte[] expectedTranspose = [1, 3, 2, 4];
            WriteMemory(memory, LoadSourceBaseAddress, source);

            MatrixTileMicroOp load = DecodeThroughProductionStageFlow(
                ref core,
                loadProgram,
                InstructionsEnum.MTILE_LOAD,
                pc: 0xB_0000UL);
            core.TestRunExecuteStageFromCurrentDecodeState();
            Require(load.LastExecutionCapture.HasValue,
                "Production execute stage did not capture MTILE_LOAD.");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 41, out _),
                "Production execute stage published MTILE_LOAD before writeback-retire.");
            core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
            Require(load.LastRetireOutcome is { IsSuccess: true },
                "Production writeback-retire did not publish MTILE_LOAD.");
            RequireTile(ref core, 41, descriptor, source);
            ReplayProductionRetire(ref core, load, InstructionsEnum.MTILE_LOAD);

            MatrixTileMicroOp store = DecodeThroughProductionStageFlow(
                ref core,
                storeProgram,
                InstructionsEnum.MTILE_STORE,
                pc: 0xB_0020UL);
            core.TestRunExecuteStageFromCurrentDecodeState();
            Require(store.LastExecutionCapture.HasValue,
                "Production execute stage did not capture MTILE_STORE.");
            Require(ReadMemory(memory, StoreDestinationBaseAddress, source.Length).All(static value => value == 0),
                "Production execute stage committed MTILE_STORE before writeback-retire.");
            core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
            Require(store.LastRetireOutcome is { IsSuccess: true },
                "Production writeback-retire did not commit MTILE_STORE.");
            Require(ReadMemory(memory, StoreDestinationBaseAddress, source.Length).SequenceEqual(source),
                "Production writeback-retire committed an incorrect MTILE_STORE image.");
            ReplayProductionRetire(ref core, store, InstructionsEnum.MTILE_STORE);

            core.SeedMatrixTileForRuntime(0, 42, runtimeRightDescriptor, [1, 0, 0, 1]);
            core.SeedMatrixTileForRuntime(0, 43, runtimeAccumulatorDescriptor, accumulatorBefore);
            MatrixTileMicroOp macc = DecodeThroughProductionStageFlow(
                ref core,
                maccProgram,
                InstructionsEnum.MTILE_MACC,
                pc: 0xB_0040UL);
            core.TestRunExecuteStageFromCurrentDecodeState();
            Require(macc.LastExecutionCapture.HasValue,
                "Production execute stage did not capture MTILE_MACC.");
            RequireTile(ref core, 43, runtimeAccumulatorDescriptor, accumulatorBefore);
            core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
            Require(macc.LastRetireOutcome is { IsSuccess: true },
                "Production writeback-retire did not publish MTILE_MACC.");
            RequireTile(ref core, 43, runtimeAccumulatorDescriptor, expectedMacc);
            ReplayProductionRetire(ref core, macc, InstructionsEnum.MTILE_MACC);

            MatrixTileMicroOp transpose = DecodeThroughProductionStageFlow(
                ref core,
                transposeProgram,
                InstructionsEnum.MTRANSPOSE,
                pc: 0xB_0060UL);
            core.TestRunExecuteStageFromCurrentDecodeState();
            Require(transpose.LastExecutionCapture.HasValue,
                "Production execute stage did not capture MTRANSPOSE.");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 44, out _),
                "Production execute stage published MTRANSPOSE before writeback-retire.");
            core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
            Require(transpose.LastRetireOutcome is { IsSuccess: true },
                "Production writeback-retire did not publish MTRANSPOSE.");
            RequireTile(ref core, 44, runtimeTransposeDescriptor, expectedTranspose);
            ReplayProductionRetire(ref core, transpose, InstructionsEnum.MTRANSPOSE);

            RequireTile(ref core, 41, descriptor, source);
            RequireTile(ref core, 43, runtimeAccumulatorDescriptor, expectedMacc);
            RequireTile(ref core, 44, runtimeTransposeDescriptor, expectedTranspose);
            Require(ReadMemory(memory, StoreDestinationBaseAddress, source.Length).SequenceEqual(source),
                "Production stage-flow replay did not restore the committed store image.");

            counters.CompilerEmissionCount = 4;
            counters.RuntimeInstructionCount = 4;
            counters.RetirePublicationCount = 4;
            counters.ReplayRoundTripCount = 4;
            counters.StreamBytesTransferred = checked((ulong)(source.Length * 2));
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, numericPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, maccLayoutPolicy.Fingerprint);
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, transposeLayoutPolicy.Fingerprint);
            stopwatch.Stop();
            return Success(
                id,
                "Production CPU stage flow / fetched compiler bundles to WB-retire",
                "Compiler-produced MatrixTile bundles enter the production decode stage and traverse production execute, memory, and writeback-retire stages; load output feeds store, MACC, and transpose.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_LOAD", "MTILE_STORE", "MTILE_MACC", "MTRANSPOSE"],
                [
                    "fetch-ingress=test-support stages serialized compiler-produced bundle bytes and lowered annotations into pipeIF",
                    "decode=production PipelineStage_Decode and canonical scheduler/materializer path",
                    "dispatch=production PipelineStage_Execute calls MatrixTileMicroOp.Execute",
                    "writeback-retire=production PipelineStage_WriteBack calls MatrixTileMicroOp.EmitWriteBackRetireRecords",
                    "retire-only=tile state and store memory remain unchanged until WB-retire",
                    "dataflow=MTILE_LOAD tile feeds MTILE_STORE, MTILE_MACC, and MTRANSPOSE",
                    "replay=all four WB-retired operations complete rollback and deterministic replay"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Production CPU stage flow / fetched compiler bundles to WB-retire", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunProductionPcFetchStageChainPressure()
    {
        const string id = "mtile-production-pc-fetch-e2e-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();
        Processor.MainMemoryArea originalMainMemory = Processor.MainMemory;
        Processor.CPU_Core originalCoreLifecycleHandle = Processor.GetCoreRef(0);

        try
        {
            MatrixTileCanonicalDescriptorAbi descriptor = MatrixTileCanonicalDescriptorAbi.Create(MatrixSide, MatrixSide, 1, MatrixSide);
            CompilerMatrixTileDescriptorAbi compilerDescriptor = CreateCompilerDescriptor(descriptor, DataTypeEnum.INT8);
            MatrixTileNumericPolicy numericPolicy = CreateRuntimeNumericPolicy(DataTypeEnum.INT8);
            MatrixTileLayoutPolicy maccLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateMaccPolicy();
            MatrixTileLayoutPolicy transposeLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicy = CreateCompilerAccumulatorPolicy(descriptor, numericPolicy, maccLayoutPolicy);
            CompilerMatrixTileTransposePolicyAbi transposePolicy = CreateCompilerTransposePolicy(descriptor, transposeLayoutPolicy);
            HybridCpuCompiledProgram loadProgram = BuildCompilerProgram(context => context.CompileMtileLoadWithDecision(CompilerMatrixTileTileOperand.Create(41), compilerDescriptor, CompilerMatrixTileMemoryFaultAbiInputs.Create(LoadSourceBaseAddress)));
            HybridCpuCompiledProgram storeProgram = BuildCompilerProgram(context => context.CompileMtileStoreWithDecision(CompilerMatrixTileTileOperand.Create(41), compilerDescriptor, CompilerMatrixTileMemoryFaultAbiInputs.Create(StoreDestinationBaseAddress)));
            HybridCpuCompiledProgram maccProgram = BuildCompilerProgram(context => context.CompileMtileMaccWithDecision(CompilerMatrixTileTileOperand.Create(41), CompilerMatrixTileTileOperand.Create(42), CompilerMatrixTileTileOperand.Create(43), compilerDescriptor, accumulatorPolicy));
            HybridCpuCompiledProgram transposeProgram = BuildCompilerProgram(context => context.CompileMtransposeWithDecision(CompilerMatrixTileTileOperand.Create(41), CompilerMatrixTileTileOperand.Create(44), compilerDescriptor, transposePolicy));
            AssertLoweredSidebands(loadProgram, InstructionsEnum.MTILE_LOAD, null, null, requireLane6: true);
            AssertLoweredSidebands(storeProgram, InstructionsEnum.MTILE_STORE, null, null, requireLane6: true);
            AssertLoweredSidebands(maccProgram, InstructionsEnum.MTILE_MACC, numericPolicy, maccLayoutPolicy, requireLane6: false);
            AssertLoweredSidebands(transposeProgram, InstructionsEnum.MTRANSPOSE, null, transposeLayoutPolicy, requireLane6: false);

            Processor.MainMemoryArea memory = CreateMemory(1);
            Processor.MainMemory = memory;
            Processor.ReplaceCore(0, CreateCore(memory));
            Processor.CPU_Core core = Processor.GetCoreRef(0);
            byte[] source = [1, 2, 3, 4];
            MatrixTileCanonicalDescriptorAbi runtimeRightDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(accumulatorPolicy.RightSourceDescriptor);
            MatrixTileCanonicalDescriptorAbi runtimeAccumulatorDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(accumulatorPolicy.AccumulatorDescriptor);
            MatrixTileCanonicalDescriptorAbi runtimeTransposeDescriptor =
                NativeTransportRuntimeAdapter.ToRuntime(transposePolicy.DestinationDescriptor);
            byte[] accumulatorBefore = new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(runtimeAccumulatorDescriptor)];
            WriteMemory(memory, LoadSourceBaseAddress, source);
            core.SeedMatrixTileForRuntime(0, 42, runtimeRightDescriptor, [1, 0, 0, 1]);
            core.SeedMatrixTileForRuntime(0, 43, runtimeAccumulatorDescriptor, accumulatorBefore);

            MatrixTileMicroOp load = ExecutePcFetchedMatrixTileStage(ref core, memory, loadProgram, InstructionsEnum.MTILE_LOAD, 0xB_0000UL, null, null);
            RequireTile(ref core, 41, descriptor, source);
            MatrixTileMicroOp store = ExecutePcFetchedMatrixTileStage(ref core, memory, storeProgram, InstructionsEnum.MTILE_STORE, 0xB_1000UL, null, null);
            Require(ReadMemory(memory, StoreDestinationBaseAddress, source.Length).SequenceEqual(source), $"{id}: store did not consume the tile loaded through the preceding PC fetch.");
            MatrixTileMicroOp macc = ExecutePcFetchedMatrixTileStage(ref core, memory, maccProgram, InstructionsEnum.MTILE_MACC, 0xB_2000UL, numericPolicy, maccLayoutPolicy);
            RequireTile(ref core, 43, runtimeAccumulatorDescriptor, PackInt32(1, 2, 3, 4));
            MatrixTileMicroOp transpose = ExecutePcFetchedMatrixTileStage(ref core, memory, transposeProgram, InstructionsEnum.MTRANSPOSE, 0xB_3000UL, null, transposeLayoutPolicy);
            RequireTile(ref core, 44, runtimeTransposeDescriptor, [1, 3, 2, 4]);
            foreach (MatrixTileMicroOp microOp in new[] { load, store, macc, transpose })
            {
                ReplayProductionRetire(ref core, microOp, (InstructionsEnum)microOp.OpCode);
            }

            ulong failClosedRejections = 0;
            int negativeCompilerEmissions = 0;

            HybridCpuCompiledProgram missingMaccNumeric = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(401),
                    CompilerMatrixTileTileOperand.Create(402),
                    CompilerMatrixTileTileOperand.Create(403),
                    compilerDescriptor,
                    accumulatorPolicy));
            failClosedRejections += ExpectPcFetchProjectionRejection(
                CloneWithLoweredAnnotations(
                    missingMaccNumeric,
                    MutateLoweredMatrixTileAnnotation(
                        missingMaccNumeric,
                        InstructionsEnum.MTILE_MACC,
                        metadata => metadata with { MatrixTileNumericPolicy = null })),
                InstructionsEnum.MTILE_MACC,
                0xB_4000UL,
                "missing MTILE_MACC numeric sideband",
                403);
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram tamperedMaccNumeric = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(411),
                    CompilerMatrixTileTileOperand.Create(412),
                    CompilerMatrixTileTileOperand.Create(413),
                    compilerDescriptor,
                    accumulatorPolicy));
            failClosedRejections += ExpectPcFetchProjectionRejection(
                CloneWithLoweredAnnotations(
                    tamperedMaccNumeric,
                    MutateLoweredMatrixTileAnnotation(
                        tamperedMaccNumeric,
                        InstructionsEnum.MTILE_MACC,
                        metadata => metadata with
                        {
                            MatrixTileNumericPolicy = metadata.MatrixTileNumericPolicy!.Value with
                            {
                                Fingerprint = metadata.MatrixTileNumericPolicy.Value.Fingerprint ^ 0xAAUL
                            }
                        })),
                InstructionsEnum.MTILE_MACC,
                0xB_5000UL,
                "tampered MTILE_MACC numeric fingerprint",
                413);
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram maccWithTransposeLayout = BuildCompilerProgram(context =>
                context.CompileMtileMaccWithDecision(
                    CompilerMatrixTileTileOperand.Create(421),
                    CompilerMatrixTileTileOperand.Create(422),
                    CompilerMatrixTileTileOperand.Create(423),
                    compilerDescriptor,
                    accumulatorPolicy));
            failClosedRejections += ExpectPcFetchProjectionRejection(
                CloneWithLoweredAnnotations(
                    maccWithTransposeLayout,
                    MutateLoweredMatrixTileAnnotation(
                        maccWithTransposeLayout,
                        InstructionsEnum.MTILE_MACC,
                        metadata => metadata with { MatrixTileLayoutPolicy = transposeLayoutPolicy })),
                InstructionsEnum.MTILE_MACC,
                0xB_6000UL,
                "MTILE_MACC with transpose layout policy",
                423);
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram transposeWithNumeric = BuildCompilerProgram(context =>
                context.CompileMtransposeWithDecision(
                    CompilerMatrixTileTileOperand.Create(431),
                    CompilerMatrixTileTileOperand.Create(432),
                    compilerDescriptor,
                    transposePolicy));
            failClosedRejections += ExpectPcFetchProjectionRejection(
                CloneWithLoweredAnnotations(
                    transposeWithNumeric,
                    MutateLoweredMatrixTileAnnotation(
                        transposeWithNumeric,
                        InstructionsEnum.MTRANSPOSE,
                        metadata => metadata with { MatrixTileNumericPolicy = numericPolicy })),
                InstructionsEnum.MTRANSPOSE,
                0xB_7000UL,
                "MTRANSPOSE with numeric sideband",
                432);
            negativeCompilerEmissions++;

            HybridCpuCompiledProgram transposeWithMaccLayout = BuildCompilerProgram(context =>
                context.CompileMtransposeWithDecision(
                    CompilerMatrixTileTileOperand.Create(441),
                    CompilerMatrixTileTileOperand.Create(442),
                    compilerDescriptor,
                    transposePolicy));
            failClosedRejections += ExpectPcFetchProjectionRejection(
                CloneWithLoweredAnnotations(
                    transposeWithMaccLayout,
                    MutateLoweredMatrixTileAnnotation(
                        transposeWithMaccLayout,
                        InstructionsEnum.MTRANSPOSE,
                        metadata => metadata with { MatrixTileLayoutPolicy = maccLayoutPolicy })),
                InstructionsEnum.MTRANSPOSE,
                0xB_8000UL,
                "MTRANSPOSE with MACC layout policy",
                442);
            negativeCompilerEmissions++;

            VerifyPcFetchReEmissionDropsStaleAnnotations(
                maccProgram,
                loadProgram,
                InstructionsEnum.MTILE_LOAD,
                0xB_9000UL);

            failClosedRejections += ExpectPcFetchBytesOverwriteWithoutRepublishRejection(
                maccProgram,
                loadProgram,
                InstructionsEnum.MTILE_LOAD,
                0xB_A000UL,
                41);
            negativeCompilerEmissions++;

            counters.CompilerEmissionCount = checked((ulong)(4 + negativeCompilerEmissions));
            counters.RuntimeInstructionCount = 4;
            counters.RetirePublicationCount = 4;
            counters.ReplayRoundTripCount = 4;
            counters.FailClosedRejectionCount = failClosedRejections;
            counters.StreamBytesTransferred = 8;
            stopwatch.Stop();
            return Success(id, "Production PC fetch / canonical compiler annotation ingress", "Four compiler-positive MatrixTile stages cross NativeTransportRuntimeAdapter.EmitProgram into real PC-driven fetch; targeted PC-fetch negatives fail closed without test-support carrier injection.", 1, counters, stopwatch.Elapsed, ["MTILE_LOAD", "MTILE_STORE", "MTILE_MACC", "MTRANSPOSE"], ["ingress=NativeTransportRuntimeAdapter.EmitProgram only", "transport=MainMemory -> L2 -> L1 -> pipeIF -> production decode", "dataflow=loaded tile feeds store/MACC/transpose", "retire-only=production WB-retire", "replay=retired results rollback and replay", "negatives=missing/tampered/mismatched sidebands fail before execute/retire", "coherence=re-emission drops stale L1/L2 carriers and raw byte overwrite without republish rejects"]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Production PC fetch / canonical compiler annotation ingress", 1, counters, stopwatch.Elapsed, ex);
        }
        finally
        {
            Processor.MainMemory = originalMainMemory;
            Processor.ReplaceCore(0, originalCoreLifecycleHandle);
        }
    }

    private static MatrixTileMicroOp ExecutePcFetchedMatrixTileStage(
        ref Processor.CPU_Core core,
        Processor.MainMemoryArea memory,
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum expectedOpcode,
        ulong emissionBase,
        MatrixTileNumericPolicy? expectedNumericPolicy,
        MatrixTileLayoutPolicy? expectedLayoutPolicy)
    {
        memory.SetLength(checked((long)(emissionBase + (ulong)compiledProgram.ProgramImage.Length)));
        NativeTransportRuntimeAdapter.EmitProgram(compiledProgram, emissionBase);
        Require(memory.TryReadVliwBundleAnnotations(emissionBase, out VliwBundleAnnotations? published) &&
                published is not null &&
                HasRuntimeTransportSideband(published),
            $"{expectedOpcode}: runtime emission did not publish the adapted lowered annotation carrier.");
        core.PrepareExecutionStart(emissionBase);
        MatrixTileMicroOp? observed = null;
        bool captureIdentityVerified = false;
        for (int cycle = 0; cycle < 32; cycle++)
        {
            core.ExecutePipelineCycle();
            if (core.TestReadDecodeStageMicroOp() is MatrixTileMicroOp microOp)
            {
                Require((InstructionsEnum)microOp.OpCode == expectedOpcode,
                    $"{expectedOpcode}: PC fetch materialized unexpected MatrixTile opcode {(InstructionsEnum)microOp.OpCode}.");
                observed = microOp;
            }

            if (!captureIdentityVerified &&
                observed?.LastExecutionCapture is MatrixTileExecutionCaptureRecord capture)
            {
                Require(Nullable.Equals(capture.NumericPolicy, expectedNumericPolicy),
                    $"{expectedOpcode}: numeric policy identity was not preserved through PC fetch.");
                Require(Nullable.Equals(capture.LayoutPolicy, expectedLayoutPolicy),
                    $"{expectedOpcode}: layout policy identity was not preserved through PC fetch.");
                captureIdentityVerified = true;
            }

            if (observed?.LastRetireOutcome is { IsSuccess: true })
            {
                Require(captureIdentityVerified,
                    $"{expectedOpcode}: WB-retired before the test observed MatrixTile capture identity.");
                (bool L1Present, bool L2Present, VliwBundleAnnotations? L1Annotations, VliwBundleAnnotations? L2Annotations) cache = core.TestReadVliwFetchCacheCarriers(emissionBase);
                bool cacheCarrierPreserved =
                    cache.L1Present &&
                    cache.L2Present &&
                    ReferenceEquals(cache.L1Annotations, published) &&
                    ReferenceEquals(cache.L2Annotations, published);
                bool conservativelyInvalidatedButSourcePreserved =
                    !cache.L1Present &&
                    !cache.L2Present &&
                    memory.TryReadVliwBundleAnnotations(
                        emissionBase,
                        out VliwBundleAnnotations? sourceAnnotations) &&
                    ReferenceEquals(sourceAnnotations, published);
                Require(
                    cacheCarrierPreserved || conservativelyInvalidatedButSourcePreserved,
                    $"{expectedOpcode}: annotation carrier was neither preserved in L2/L1 nor conservatively invalidated with its MainMemory source intact.");
                return observed;
            }
        }

        (bool executeValid, bool resultReady, bool vectorComplete) = core.TestReadExecuteStageStatus();
        (bool lane6Occupied, bool lane6ResultReady, bool lane6VectorComplete, string? lane6MicroOp) =
            core.TestReadExecuteLaneStatus(6);
        (bool lane0Occupied, bool lane0ResultReady, bool lane0VectorComplete, string? lane0MicroOp) =
            core.TestReadExecuteLaneStatus(0);
        (bool decodeValid, uint decodedOpcode, _, _) = core.TestReadDecodeStageStatus();
        throw new InvalidOperationException(
            $"{expectedOpcode}: PC-driven fetch/decode/execute/WB-retire did not complete within the production cycle budget " +
            $"(observed={observed is not null}, captured={observed?.LastExecutionCapture.HasValue}, retire={observed?.LastRetireOutcome.HasValue}, " +
            $"decodeValid={decodeValid}, decodeOpcode=0x{decodedOpcode:X}, executeValid={executeValid}, resultReady={resultReady}, vectorComplete={vectorComplete}, " +
            $"lane0={lane0MicroOp}/{lane0Occupied}/{lane0ResultReady}/{lane0VectorComplete}, " +
            $"lane6={lane6MicroOp}/{lane6Occupied}/{lane6ResultReady}/{lane6VectorComplete}).");
    }

    private static HybridCpuCompiledProgram CloneWithLoweredAnnotations(
        HybridCpuCompiledProgram compiledProgram,
        IReadOnlyList<VliwBundleAnnotations> loweredBundleAnnotations)
    {
        return new HybridCpuCompiledProgram(
            compiledProgram.ProgramSchedule,
            compiledProgram.BundleLayout,
            compiledProgram.LoweredBundles,
            compiledProgram.ProgramImage,
            compiledProgram.ContractVersion,
            compiledProgram.EmissionBaseAddress,
            compiledProgram.AdmissibilityAgreement,
            NativeTransportRuntimeAdapter.ToCore(loweredBundleAnnotations));
    }

    private static ulong ExpectPcFetchProjectionRejection(
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum expectedOpcode,
        ulong emissionBase,
        string expectation,
        params ushort[] forbiddenTileIds)
    {
        Processor.MainMemoryArea memory = CreateMemory(1);
        Processor.MainMemory = memory;
        Processor.ReplaceCore(0, CreateCore(memory));
        Processor.CPU_Core core = Processor.GetCoreRef(0);
        memory.SetLength(checked((long)(emissionBase + (ulong)compiledProgram.ProgramImage.Length)));
        NativeTransportRuntimeAdapter.EmitProgram(compiledProgram, emissionBase);
        core.PrepareExecutionStart(emissionBase);

        bool rejected = false;
        MatrixTileMicroOp? observed = null;
        try
        {
            for (int cycle = 0; cycle < 16; cycle++)
            {
                core.ExecutePipelineCycle();
                if (core.TestReadDecodeStageMicroOp() is MatrixTileMicroOp microOp)
                {
                    observed = microOp;
                    Require((InstructionsEnum)microOp.OpCode == expectedOpcode,
                        $"{expectation}: PC fetch decoded unexpected opcode {(InstructionsEnum)microOp.OpCode}.");
                    Require(!microOp.LastExecutionCapture.HasValue && !microOp.LastRetireOutcome.HasValue,
                        $"{expectation}: invalid PC-fetch sideband reached execute or retire.");
                }

                Require(observed is null || (!observed.LastExecutionCapture.HasValue && !observed.LastRetireOutcome.HasValue),
                    $"{expectation}: invalid PC-fetch sideband reached MatrixTile capture or retire.");
            }
        }
        catch
        {
            rejected = true;
        }

        Require(observed is null || (!observed.LastExecutionCapture.HasValue && !observed.LastRetireOutcome.HasValue),
            $"{expectation}: rejection occurred after MatrixTile capture or retire.");
        foreach (ushort tileId in forbiddenTileIds)
        {
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, tileId, out _),
                $"{expectation}: invalid PC-fetch ingress published tile {tileId}.");
        }

        _ = rejected;
        return 1;
    }

    private static ulong ExpectPcFetchBytesOverwriteWithoutRepublishRejection(
        HybridCpuCompiledProgram staleCarrierProgram,
        HybridCpuCompiledProgram overwrittenBytesProgram,
        InstructionsEnum overwrittenOpcode,
        ulong emissionBase,
        ushort forbiddenTileId)
    {
        Processor.MainMemoryArea memory = CreateMemory(1);
        Processor.MainMemory = memory;
        Processor.ReplaceCore(0, CreateCore(memory));
        Processor.CPU_Core core = Processor.GetCoreRef(0);
        memory.SetLength(checked((long)(emissionBase + (ulong)staleCarrierProgram.ProgramImage.Length)));

        NativeTransportRuntimeAdapter.EmitProgram(staleCarrierProgram, emissionBase);
        core.PrepareExecutionStart(emissionBase);
        core.ExecutePipelineCycle();
        (bool L1Present, bool L2Present, VliwBundleAnnotations? L1Annotations, VliwBundleAnnotations? L2Annotations) warmedCache = core.TestReadVliwFetchCacheCarriers(emissionBase);
        Require(warmedCache.L1Present && warmedCache.L2Present,
            "bundle overwrite negative failed to warm the stale VLIW cache carrier.");

        memory.WriteToPosition(overwrittenBytesProgram.ProgramImage, emissionBase);
        core.InvalidateVliwFetchState(emissionBase);
        Require(!memory.TryReadVliwBundleAnnotations(emissionBase, out _),
            "bundle bytes overwritten without republish left a stale MainMemory annotation carrier.");

        core.PrepareExecutionStart(emissionBase);
        bool rejected = false;
        MatrixTileMicroOp? observed = null;
        try
        {
            for (int cycle = 0; cycle < 16; cycle++)
            {
                core.ExecutePipelineCycle();
                if (core.TestReadDecodeStageMicroOp() is MatrixTileMicroOp microOp)
                {
                    observed = microOp;
                    Require((InstructionsEnum)microOp.OpCode == overwrittenOpcode,
                        $"bundle bytes overwritten without republish decoded unexpected opcode {(InstructionsEnum)microOp.OpCode}.");
                    Require(!microOp.LastExecutionCapture.HasValue && !microOp.LastRetireOutcome.HasValue,
                        "bundle bytes overwritten without republish reached execute or retire.");
                }

                Require(observed is null || (!observed.LastExecutionCapture.HasValue && !observed.LastRetireOutcome.HasValue),
                    "bundle bytes overwritten without republish reached MatrixTile capture or retire.");
            }
        }
        catch
        {
            rejected = true;
        }

        Require(observed is null || (!observed.LastExecutionCapture.HasValue && !observed.LastRetireOutcome.HasValue),
            "bundle bytes overwritten without republish rejected after MatrixTile side effects.");
        Require(!core.TryCaptureAnyMatrixTileSnapshot(0, forbiddenTileId, out _),
            $"bundle bytes overwritten without republish published tile {forbiddenTileId}.");
        _ = rejected;
        return 1;
    }

    private static void VerifyPcFetchReEmissionDropsStaleAnnotations(
        HybridCpuCompiledProgram staleCarrierProgram,
        HybridCpuCompiledProgram replacementProgram,
        InstructionsEnum replacementOpcode,
        ulong emissionBase)
    {
        Processor.MainMemoryArea memory = CreateMemory(1);
        Processor.MainMemory = memory;
        Processor.ReplaceCore(0, CreateCore(memory));
        Processor.CPU_Core core = Processor.GetCoreRef(0);
        memory.SetLength(checked((long)(emissionBase + (ulong)staleCarrierProgram.ProgramImage.Length)));

        NativeTransportRuntimeAdapter.EmitProgram(staleCarrierProgram, emissionBase);
        core.PrepareExecutionStart(emissionBase);
        core.ExecutePipelineCycle();
        (bool L1Present, bool L2Present, VliwBundleAnnotations? L1Annotations, VliwBundleAnnotations? L2Annotations) warmedCache = core.TestReadVliwFetchCacheCarriers(emissionBase);
        Require(warmedCache.L1Present && warmedCache.L2Present,
            "re-emission stale annotation check failed to warm VLIW cache carriers.");

        NativeTransportRuntimeAdapter.EmitProgram(replacementProgram, emissionBase);

        Require(memory.TryReadVliwBundleAnnotations(emissionBase, out VliwBundleAnnotations? replacementAnnotations) &&
                replacementAnnotations is not null &&
                HasRuntimeTransportSideband(replacementAnnotations) &&
                !ReferenceEquals(replacementAnnotations, warmedCache.L1Annotations),
            "re-emission did not replace MainMemory with a freshly adapted annotation carrier.");
        (bool L1Present, bool L2Present, VliwBundleAnnotations? L1Annotations, VliwBundleAnnotations? L2Annotations) cacheAfterReEmission = core.TestReadVliwFetchCacheCarriers(emissionBase);
        Require(!cacheAfterReEmission.L1Present && !cacheAfterReEmission.L2Present,
            "re-emission left stale L1/L2 VLIW annotation carriers.");

        MatrixTileMicroOp microOp = ExecutePcFetchedMatrixTileStage(
            ref core,
            memory,
            replacementProgram,
            replacementOpcode,
            emissionBase,
            null,
            null);
        Require((InstructionsEnum)microOp.OpCode == replacementOpcode,
            "re-emission fetched a stale opcode after replacement.");
    }

    private static MatrixTileSpecScenarioReport RunFailClosedPressure()
    {
        const string id = "mtile-fail-closed-policy-and-resource-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Processor.MainMemoryArea memory = CreateMemory(1);
            Processor.CPU_Core core = CreateCore(memory);

            MatrixTileMicroOp macc = CreateMaccMicroOp(1, 2, 3);
            byte[] accumulatorBefore = new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(macc.ResultTileDescriptor)];
            core.SeedMatrixTileForRuntime(0, 1, macc.TileDescriptor, [1, 2, 3, 4]);
            core.SeedMatrixTileForRuntime(0, 2, macc.SecondaryTileDescriptor, [1, 0, 0, 1]);
            core.SeedMatrixTileForRuntime(0, 3, macc.ResultTileDescriptor, accumulatorBefore);
            MatrixTileExecutionCaptureRecord maccCapture = ExecuteAndGetCapture(ref core, macc);
            MatrixTileExecutionCaptureRecord tamperedPolicy = maccCapture with
            {
                NumericPolicy = maccCapture.NumericPolicy!.Value with
                {
                    Fingerprint = maccCapture.NumericPolicy.Value.Fingerprint ^ 0x10UL
                }
            };
            ExpectRetireRejection(ref core, macc, tamperedPolicy, "tampered MACC numeric policy");
            RequireTile(ref core, 3, macc.ResultTileDescriptor, accumulatorBefore);
            counters.FailClosedRejectionCount++;

            MatrixTileExecutionCaptureRecord wrongOwner = maccCapture with
            {
                CaptureIdentity = maccCapture.CaptureIdentity with
                {
                    OwnerThreadId = 1
                }
            };
            ExpectRetireRejection(ref core, macc, wrongOwner, "wrong-owner MACC capture");
            RequireTile(ref core, 3, macc.ResultTileDescriptor, accumulatorBefore);
            counters.FailClosedRejectionCount++;

            Processor.CPU_Core foreignCore = CreateCore(memory, coreId: 1);
            ExpectRetireRejection(ref foreignCore, macc, maccCapture, "cross-core MACC capture");
            RequireTile(ref core, 3, macc.ResultTileDescriptor, accumulatorBefore);
            counters.FailClosedRejectionCount++;

            byte[] loadBytes = [5, 6, 7, 8];
            WriteMemory(memory, LoadSourceBaseAddress, loadBytes);
            MatrixTileMicroOp load = CreateMemoryMicroOp(InstructionsEnum.MTILE_LOAD, LoadSourceBaseAddress, 4);
            MatrixTileExecutionCaptureRecord loadCapture = ExecuteAndGetCapture(ref core, load);
            MatrixTileExecutionCaptureRecord tamperedTransfer = loadCapture with
            {
                StreamTransfer = loadCapture.StreamTransfer with
                {
                    ResourceClass = MatrixTileRuntimeResourceClass.MatrixTileCompute
                }
            };
            ExpectRetireRejection(ref core, load, tamperedTransfer, "tampered MatrixTile stream resource class");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 4, out _),
                "Rejected typed-transfer tamper published a tile image.");
            counters.FailClosedRejectionCount++;

            MatrixTileExecutionCaptureRecord tamperedDirection = loadCapture with
            {
                StreamTransfer = loadCapture.StreamTransfer with
                {
                    Direction = MatrixTileStreamTransferDirection.TileEgress
                }
            };
            ExpectRetireRejection(ref core, load, tamperedDirection, "tampered MatrixTile stream direction");
            Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 4, out _),
                "Rejected typed-direction tamper published a tile image.");
            counters.FailClosedRejectionCount++;

            Require(MatrixTileResourceContour.ResolveSlotClass(MatrixTileRuntimeResourceClass.MatrixTileMemory) ==
                    SlotClass.MatrixTileStreamClass,
                "MatrixTile memory contour did not resolve to MatrixTileStreamClass.");
            Require(SlotClassLaneMap.GetLaneMask(SlotClass.MatrixTileStreamClass) ==
                    SlotClassLaneMap.GetLaneMask(SlotClass.DmaStreamClass),
                "MatrixTileStreamClass and DmaStreamClass no longer share physical lane6.");
            SlotClassCapacityState capacity = new SlotClassCapacityState();
            capacity.InitializeFromLaneMap();
            capacity.IncrementOccupancy(SlotClass.MatrixTileStreamClass);
            Require(!capacity.HasFreeCapacity(SlotClass.DmaStreamClass),
                "DmaStreamClass remained admissible while MatrixTileStreamClass held lane6 capacity.");
            counters.FailClosedRejectionCount++;

            Processor.CPU_Core staleCore = CreateCore(CreateMemory(1));
            MatrixTileMicroOp staleMacc = CreateMaccMicroOp(11, 12, 13);
            byte[] staleAccumulatorBefore = new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(staleMacc.ResultTileDescriptor)];
            staleCore.SeedMatrixTileForRuntime(0, 11, staleMacc.TileDescriptor, [1, 1, 1, 1]);
            staleCore.SeedMatrixTileForRuntime(0, 12, staleMacc.SecondaryTileDescriptor, [1, 0, 0, 1]);
            staleCore.SeedMatrixTileForRuntime(0, 13, staleMacc.ResultTileDescriptor, staleAccumulatorBefore);
            MatrixTileExecutionCaptureRecord staleCapture = ExecuteAndGetCapture(ref staleCore, staleMacc);
            staleCore.AdvanceMatrixTileReplayInvalidationEpochForTesting();
            ExpectRetireRejection(ref staleCore, staleMacc, staleCapture, "stale epoch MACC capture");
            RequireTile(ref staleCore, 13, staleMacc.ResultTileDescriptor, staleAccumulatorBefore);
            counters.FailClosedRejectionCount++;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, (ulong)counters.FailClosedRejectionCount);

            stopwatch.Stop();
            return Success(
                id,
                "Fail-closed runtime validation",
                "Tampered policy, wrong-owner/cross-core capture, stale epoch, typed-transfer identity rejection, and physical lane6 capacity conflict with DmaStreamClass.",
                1,
                counters,
                stopwatch.Elapsed,
                ["MTILE_MACC", "MTILE_LOAD"],
                [
                    "retire rejects tampered policy before publication",
                    "retire rejects wrong-owner capture identity before publication",
                    "retire rejects cross-core capture identity before publication",
                    "retire rejects stale epoch capture identity before publication",
                    "retire rejects wrong MatrixTile stream resource class",
                    "retire rejects wrong MatrixTile stream direction",
                    "MatrixTileStreamClass aliases DmaStreamClass capacity on lane6"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "Fail-closed runtime validation", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileSpecScenarioReport RunFaultFuzzPressure()
    {
        const string id = "mtile-fault-fuzz-policy-identity-pressure";
        ScenarioCounters counters = new ScenarioCounters();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Processor.MainMemoryArea memory = CreateMemory(1);
            Processor.CPU_Core core = CreateCore(memory);
            MatrixTileMicroOp macc = CreateMaccMicroOp(21, 22, 23);
            byte[] accumulatorBefore = new byte[MatrixTileExecuteCaptureAbi.GetPackedByteLength(macc.ResultTileDescriptor)];
            core.SeedMatrixTileForRuntime(0, 21, macc.TileDescriptor, [1, 2, 3, 4]);
            core.SeedMatrixTileForRuntime(0, 22, macc.SecondaryTileDescriptor, [1, 0, 0, 1]);
            core.SeedMatrixTileForRuntime(0, 23, macc.ResultTileDescriptor, accumulatorBefore);
            MatrixTileExecutionCaptureRecord cleanMaccCapture = ExecuteAndGetCapture(ref core, macc);

            (string Name, Func<MatrixTileExecutionCaptureRecord, MatrixTileExecutionCaptureRecord> Mutate)[] maccMutations =
            [
                (
                    "missing numeric policy",
                    capture => capture with { NumericPolicy = null }
                ),
                (
                    "tampered layout fingerprint",
                    capture => capture with
                    {
                        LayoutPolicy = capture.LayoutPolicy!.Value with
                        {
                            Fingerprint = capture.LayoutPolicy.Value.Fingerprint ^ 0x22UL
                        }
                    }
                ),
                (
                    "wrong operation kind identity",
                    capture => capture with
                    {
                        CaptureIdentity = capture.CaptureIdentity with
                        {
                            OperationKind = MatrixTileProjectedOperationKind.Transpose
                        }
                    }
                ),
                (
                    "wrong opcode identity",
                    capture => capture with
                    {
                        CaptureIdentity = capture.CaptureIdentity with
                        {
                            Opcode = (uint)InstructionsEnum.MTILE_LOAD
                        }
                    }
                )
            ];

            foreach ((string name, Func<MatrixTileExecutionCaptureRecord, MatrixTileExecutionCaptureRecord> mutate) in maccMutations)
            {
                ExpectRetireRejection(ref core, macc, mutate(cleanMaccCapture), name);
                RequireTile(ref core, 23, macc.ResultTileDescriptor, accumulatorBefore);
                counters.FailClosedRejectionCount++;
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(System.Text.Encoding.UTF8.GetBytes(name)));
            }

            byte[] loadBytes = [9, 8, 7, 6];
            WriteMemory(memory, LoadSourceBaseAddress, loadBytes);
            MatrixTileMicroOp load = CreateMemoryMicroOp(InstructionsEnum.MTILE_LOAD, LoadSourceBaseAddress, 24);
            MatrixTileExecutionCaptureRecord cleanLoadCapture = ExecuteAndGetCapture(ref core, load);
            (string Name, Func<MatrixTileExecutionCaptureRecord, MatrixTileExecutionCaptureRecord> Mutate)[] loadMutations =
            [
                (
                    "load wrong owner thread",
                    capture => capture with
                    {
                        CaptureIdentity = capture.CaptureIdentity with { OwnerThreadId = 3 }
                    }
                ),
                (
                    "load wrong transfer channel",
                    capture => capture with
                    {
                        StreamTransfer = capture.StreamTransfer with { StreamEngineChannel = checked((byte)(capture.StreamTransfer.StreamEngineChannel + 1)) }
                    }
                ),
                (
                    "load wrong transfer operation",
                    capture => capture with
                    {
                        StreamTransfer = capture.StreamTransfer with { OperationKind = MatrixTileProjectedOperationKind.Store }
                    }
                )
            ];

            foreach ((string name, Func<MatrixTileExecutionCaptureRecord, MatrixTileExecutionCaptureRecord> mutate) in loadMutations)
            {
                ExpectRetireRejection(ref core, load, mutate(cleanLoadCapture), name);
                Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 24, out _),
                    $"{name}: rejected load mutation published tile state.");
                counters.FailClosedRejectionCount++;
                counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(System.Text.Encoding.UTF8.GetBytes(name)));
            }

            MatrixTileExecutionCaptureRecord zeroOrdinal = cleanMaccCapture with
            {
                CaptureIdentity = cleanMaccCapture.CaptureIdentity with
                {
                    CaptureOrdinal = 0
                }
            };
            ExpectRetireRejection(ref core, macc, zeroOrdinal, "zero capture ordinal");
            RequireTile(ref core, 23, macc.ResultTileDescriptor, accumulatorBefore);
            counters.FailClosedRejectionCount++;
            counters.ResultChecksum = MixChecksum(counters.ResultChecksum, cleanMaccCapture.CaptureIdentity.CaptureFingerprint);

            stopwatch.Stop();
            return Success(
                id,
                "MatrixTile fault fuzz / policy identity and descriptor negatives",
                "Table-driven negative MatrixTile capture mutations cover missing numeric policy, tampered layout, wrong operation/opcode identity, zero capture ordinal, and load owner/channel/operation tamper.",
                checked((ulong)(maccMutations.Length + loadMutations.Length + 1)),
                counters,
                stopwatch.Elapsed,
                ["MTILE_MACC", "MTILE_LOAD"],
                [
                    "fuzz=missing/tampered numeric-layout policy identity",
                    "fuzz=wrong operation/opcode and zero ordinal identity",
                    "fuzz=load owner/channel/operation transfer identity",
                    "fuzz=no publication after rejected mutations"
                ]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(id, "MatrixTile fault fuzz / policy identity and descriptor negatives", 1, counters, stopwatch.Elapsed, ex);
        }
    }

    private static MatrixTileMicroOp CreateMemoryMicroOp(
        InstructionsEnum opcode,
        ulong memoryAddress,
        ushort tileId) =>
        CreateMicroOp(opcode, memoryAddress, tileId, numericPolicy: null, layoutPolicy: null);

    private static MatrixTileMicroOp CreateMemoryMicroOp(
        InstructionsEnum opcode,
        ulong memoryAddress,
        ushort tileId,
        ushort rows,
        ushort columns,
        uint rowStride) =>
        CreateMicroOp(
            opcode,
            memoryAddress,
            tileId,
            numericPolicy: null,
            layoutPolicy: null,
            rows,
            columns,
            rowStride);

    private static MatrixTileMicroOp CreateMaccMicroOp(
        ushort leftTileId,
        ushort rightTileId,
        ushort accumulatorTileId) =>
        CreateMicroOp(
            InstructionsEnum.MTILE_MACC,
            leftTileId,
            ((ulong)accumulatorTileId << 16) | rightTileId,
            MatrixTileNumericPolicyAbi.CreateSupportedPolicy(MatrixTileNumericProfileId.SignedInt8ToInt32),
            MatrixTileLayoutPolicyAbi.CreateMaccPolicy(),
            MatrixSide,
            MatrixSide,
            MatrixSide);

    private static MatrixTileMicroOp CreateTransposeMicroOp(
        ushort sourceTileId,
        ushort destinationTileId) =>
        CreateMicroOp(
            InstructionsEnum.MTRANSPOSE,
            sourceTileId,
            destinationTileId,
            numericPolicy: null,
            MatrixTileLayoutPolicyAbi.CreateTransposePolicy(),
            MatrixSide,
            MatrixSide,
            MatrixSide);

    private static MatrixTileMicroOp CreateMicroOp(
        InstructionsEnum opcode,
        ulong primaryPointer,
        ulong secondaryPointer,
        MatrixTileNumericPolicy? numericPolicy,
        MatrixTileLayoutPolicy? layoutPolicy) =>
        CreateMicroOp(
            opcode,
            primaryPointer,
            secondaryPointer,
            numericPolicy,
            layoutPolicy,
            MatrixSide,
            MatrixSide,
            MatrixSide);

    private static MatrixTileMicroOp CreateMicroOp(
        InstructionsEnum opcode,
        ulong primaryPointer,
        ulong secondaryPointer,
        MatrixTileNumericPolicy? numericPolicy,
        MatrixTileLayoutPolicy? layoutPolicy,
        ushort rows,
        ushort columns,
        uint rowStride)
    {
        DecoderContext context = new DecoderContext
        {
            OpCode = (uint)opcode,
            Immediate = columns,
            HasImmediate = true,
            DataType = (byte)DataTypeEnum.INT8,
            HasDataType = true,
            VectorPrimaryPointer = primaryPointer,
            VectorSecondaryPointer = secondaryPointer,
            VectorStreamLength = checked((uint)(rows * columns)),
            VectorStride = 1,
            VectorRowStride = checked((ushort)rowStride),
            MatrixTileNumericPolicy = numericPolicy,
            MatrixTileLayoutPolicy = layoutPolicy,
            HasVectorPayload = true,
            HasVectorAddressingContour = true,
            Is2DAddressing = true,
            IndexedAddressing = false,
            PredicateMask = 0
        };

        return InstructionRegistry.CreateMicroOp((uint)opcode, context) as MatrixTileMicroOp
            ?? throw new InvalidOperationException($"{opcode} did not materialize as a typed MatrixTileMicroOp.");
    }

    private static void AssertSourceSidebands(
        HybridCpuThreadCompilerContext context,
        int sourceSlot,
        InstructionsEnum opcode,
        MatrixTileNumericPolicy? expectedNumericPolicy,
        MatrixTileLayoutPolicy? expectedLayoutPolicy)
    {
        Require(
            context.GetBundleAnnotations().TryGetInstructionSlotMetadata(sourceSlot, out InstructionSlotMetadata metadata),
            $"{opcode}: source InstructionSlotMetadata was not emitted.");
        AssertExpectedSidebands(opcode, metadata, expectedNumericPolicy, expectedLayoutPolicy, "source");
    }

    private static void AssertLoweredSidebands(
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum opcode,
        MatrixTileNumericPolicy? expectedNumericPolicy,
        MatrixTileLayoutPolicy? expectedLayoutPolicy,
        bool requireLane6)
    {
        for (int bundleIndex = 0; bundleIndex < compiledProgram.LoweredBundles.Count; bundleIndex++)
        {
            HybridCpuInstructionBundle compilerBundle = compiledProgram.LoweredBundles[bundleIndex];
            VLIW_Bundle bundle = NativeTransportRuntimeAdapter.ToRuntime(in compilerBundle);
            VliwBundleAnnotations annotations =
                NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundleAnnotations[bundleIndex]);
            for (int slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
            {
                VLIW_Instruction instruction = bundle.GetInstruction(slotIndex);
                if ((InstructionsEnum)instruction.OpCode != opcode)
                {
                    continue;
                }

                Require(
                    annotations.TryGetInstructionSlotMetadata(slotIndex, out InstructionSlotMetadata metadata),
                    $"{opcode}: lowered InstructionSlotMetadata was not preserved.");
                AssertExpectedSidebands(opcode, metadata, expectedNumericPolicy, expectedLayoutPolicy, "lowered");
                if (requireLane6)
                {
                    Require(slotIndex == MatrixTileResourceContour.TileStreamLaneId,
                        $"{opcode}: lowered MatrixTile memory transport was not placed on lane6.");
                }

                return;
            }
        }

        throw new InvalidOperationException($"{opcode}: lowered instruction was not found.");
    }

    private static bool HasRuntimeTransportSideband(VliwBundleAnnotations annotations)
    {
        for (byte slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
        {
            if (!annotations.TryGetInstructionSlotMetadata(slotIndex, out InstructionSlotMetadata metadata))
            {
                continue;
            }

            if (metadata != InstructionSlotMetadata.Default)
            {
                return true;
            }
        }

        return false;
    }

    private static MatrixTileMicroOp MaterializeLoweredMatrixTileMicroOp(
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum opcode)
    {
        for (int bundleIndex = 0; bundleIndex < compiledProgram.LoweredBundles.Count; bundleIndex++)
        {
            HybridCpuInstructionBundle compilerBundle = compiledProgram.LoweredBundles[bundleIndex];
            VLIW_Bundle bundle = NativeTransportRuntimeAdapter.ToRuntime(in compilerBundle);
            VliwBundleAnnotations annotations =
                NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundleAnnotations[bundleIndex]);
            VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
            for (int slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
            {
                rawSlots[slotIndex] = bundle.GetInstruction(slotIndex);
            }

            VliwDecoderV4 decoder = new VliwDecoderV4();
            DecodedInstructionBundle decodedBundle = decoder.DecodeInstructionBundle(
                rawSlots,
                annotations,
                bundleAddress: checked((ulong)(0x8_0000 + (bundleIndex * 0x20))),
                bundleSerial: checked((ulong)(200 + bundleIndex)));
            MicroOp?[] carriers =
                DecodedBundleTransportProjector.BuildCanonicalCarrierBundleForTesting(
                    rawSlots,
                    decodedBundle);

            for (int slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
            {
                if ((InstructionsEnum)rawSlots[slotIndex].OpCode != opcode)
                {
                    continue;
                }

                return carriers[slotIndex] as MatrixTileMicroOp
                    ?? throw new InvalidOperationException(
                        $"{opcode}: lowered slot {slotIndex} did not materialize as a MatrixTileMicroOp.");
            }
        }

        throw new InvalidOperationException($"{opcode}: lowered instruction was not found for runtime materialization.");
    }

    private static HybridCpuCompiledProgram BuildCompilerProgram(
        Action<HybridCpuThreadCompilerContext> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        emit(context);
        return context.CompileProgram();
    }

    private static CompilerMatrixTileDescriptorAbi CreateCompilerDescriptor(
        MatrixTileCanonicalDescriptorAbi descriptor,
        DataTypeEnum elementType) =>
        new(
            NativeTransportRuntimeAdapter.ToCore(descriptor),
            NativeTransportRuntimeAdapter.ToCore(elementType));

    private static MatrixTileNumericPolicy CreateRuntimeNumericPolicy(DataTypeEnum elementType) =>
        NativeTransportRuntimeAdapter.ToRuntime(
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(
                NativeTransportRuntimeAdapter.ToCore(elementType)));

    private static CompilerMatrixTileAccumulatorPolicyAbi CreateCompilerAccumulatorPolicy(
        MatrixTileCanonicalDescriptorAbi descriptor,
        MatrixTileNumericPolicy numericPolicy,
        MatrixTileLayoutPolicy layoutPolicy) =>
        CompilerMatrixTileAccumulatorPolicyAbi.CreateForRuntimeDerivedFootprint(
            NativeTransportRuntimeAdapter.ToCore(descriptor),
            NativeTransportRuntimeAdapter.ToCore(numericPolicy),
            NativeTransportRuntimeAdapter.ToCore(layoutPolicy));

    private static CompilerMatrixTileTransposePolicyAbi CreateCompilerTransposePolicy(
        MatrixTileCanonicalDescriptorAbi descriptor,
        MatrixTileLayoutPolicy layoutPolicy) =>
        CompilerMatrixTileTransposePolicyAbi.CreateForRuntimeDerivedDestination(
            NativeTransportRuntimeAdapter.ToCore(descriptor)) with
        {
            MatrixTileLayoutPolicy = NativeTransportRuntimeAdapter.ToCore(layoutPolicy)
        };

    private static MatrixTileMicroOp DecodeThroughProductionStageFlow(
        ref Processor.CPU_Core core,
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum opcode,
        ulong pc)
    {
        for (int bundleIndex = 0; bundleIndex < compiledProgram.LoweredBundles.Count; bundleIndex++)
        {
            HybridCpuInstructionBundle compilerBundle = compiledProgram.LoweredBundles[bundleIndex];
            VLIW_Bundle bundle = NativeTransportRuntimeAdapter.ToRuntime(in compilerBundle);
            VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
            bool containsOpcode = false;
            for (int slotIndex = 0; slotIndex < rawSlots.Length; slotIndex++)
            {
                rawSlots[slotIndex] = bundle.GetInstruction(slotIndex);
                containsOpcode |= (InstructionsEnum)rawSlots[slotIndex].OpCode == opcode;
            }

            if (!containsOpcode)
            {
                continue;
            }

            core.TestRunDecodeStageWithFetchedBundle(
                rawSlots,
                pc,
                NativeTransportRuntimeAdapter.ToRuntime(
                    compiledProgram.LoweredBundleAnnotations[bundleIndex]));
            (bool valid, uint decodedOpcode, _, _) = core.TestReadDecodeStageStatus();
            Require(valid && (InstructionsEnum)decodedOpcode == opcode,
                $"Production decode stage did not issue {opcode} from the compiler-produced fetched bundle.");
            MicroOp? decodedMicroOp = core.TestReadDecodeStageMicroOp();
            Require(decodedMicroOp is MatrixTileMicroOp,
                $"Production decode stage did not materialize {opcode} as MatrixTileMicroOp.");
            MatrixTileMicroOp matrixTileMicroOp = (MatrixTileMicroOp)decodedMicroOp!;
            Require(matrixTileMicroOp.MaterializedInstruction.IsRuntimeLegal &&
                    matrixTileMicroOp.Projection.FaultKind == MatrixTileIrProjectionFaultKind.None,
                $"Production decode stage materialized {opcode} with a runtime projection fault.");
            return matrixTileMicroOp;
        }

        throw new InvalidOperationException(
            $"{opcode}: compiler-produced lowered bundle was not found for production stage-flow decode.");
    }

    private static void ReplayProductionRetire(
        ref Processor.CPU_Core core,
        MatrixTileMicroOp microOp,
        InstructionsEnum opcode)
    {
        MatrixTileReplayRollbackJournal journal = microOp.LastReplayRollbackJournal
            ?? throw new InvalidOperationException(
                $"Production writeback-retire did not publish a replay journal for {opcode}.");
        MatrixTileRollbackOutcome rollback =
            microOp.RollbackRetiredResult(ref core, journal.ReplayIdentity);
        bool restoredExpectedSurface = opcode == InstructionsEnum.MTILE_STORE
            ? rollback.RestoredMemory
            : rollback.RestoredTileState;
        Require(rollback.Lifecycle == MatrixTileReplayRollbackLifecycle.RolledBack &&
                restoredExpectedSurface,
            $"Production writeback-retired {opcode} did not rollback its publication surface.");

        MatrixTileRetireOutcome replay =
            microOp.ReplayRolledBackResult(ref core, journal.ReplayIdentity);
        Require(replay.IsSuccess &&
                microOp.CaptureLifecycle == MatrixTileCaptureLifecycle.Replayed,
            $"Production writeback-retired {opcode} did not replay deterministically.");
    }

    private static MatrixTileFullPipelineStepEvidence RequirePipelineStep(
        MatrixTileFullPipelineReport report,
        InstructionsEnum opcode)
    {
        MatrixTileFullPipelineStepEvidence? match = null;
        foreach (MatrixTileFullPipelineStepEvidence step in report.Steps)
        {
            if (step.Opcode != opcode)
            {
                continue;
            }

            Require(match is null, $"Full pipeline report contains duplicate {opcode} evidence.");
            match = step;
        }

        return match ?? throw new InvalidOperationException(
            $"Full pipeline report does not contain {opcode} evidence.");
    }

    private static IReadOnlyList<VliwBundleAnnotations> MutateLoweredMatrixTileAnnotation(
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum opcode,
        Func<InstructionSlotMetadata, InstructionSlotMetadata> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        VliwBundleAnnotations[] result = new VliwBundleAnnotations[compiledProgram.LoweredBundleAnnotations.Count];
        bool mutated = false;

        for (int bundleIndex = 0; bundleIndex < result.Length; bundleIndex++)
        {
            VliwBundleAnnotations source =
                NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundleAnnotations[bundleIndex]);
            InstructionSlotMetadata[] slotMetadata = new InstructionSlotMetadata[BundleMetadata.BundleSlotCount];
            for (int slotIndex = 0; slotIndex < slotMetadata.Length; slotIndex++)
            {
                slotMetadata[slotIndex] = source.TryGetInstructionSlotMetadata(
                    slotIndex,
                    out InstructionSlotMetadata metadata)
                    ? metadata
                    : InstructionSlotMetadata.Default;

                if (!mutated &&
                    (InstructionsEnum)compiledProgram.LoweredBundles[bundleIndex]
                        .GetInstruction(slotIndex)
                        .OpCode == opcode)
                {
                    slotMetadata[slotIndex] = mutate(slotMetadata[slotIndex]);
                    mutated = true;
                }
            }

            result[bundleIndex] = new VliwBundleAnnotations(slotMetadata);
        }

        Require(mutated, $"Cannot mutate lowered sideband because {opcode} was not found.");
        return result;
    }

    private static ulong ExpectFullPipelineProjectionRejection(
        HybridCpuCompiledProgram compiledProgram,
        IReadOnlyList<VliwBundleAnnotations> annotations,
        string expectation)
    {
        Processor.MainMemoryArea memory = CreateMemory(1);
        Processor.CPU_Core core = CreateCore(memory);
        MatrixTileFullPipelineReport report =
            MatrixTileFullPipelineHarness.RunCompilerLoweredBundlesForTesting(
                ref core,
                NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundles),
                annotations,
                new MatrixTileFullPipelineHarnessOptions
                {
                    ReplayAfterSuccessfulRetire = false
                });

        Require(report.Steps.Count == 1, $"{expectation}: expected one full pipeline evidence step.");
        MatrixTileFullPipelineStepEvidence step = report.Steps[0];
        Require(step.FailClosedRejected,
            $"{expectation}: full pipeline did not reject the invalid transport.");
        Require(step.FetchObserved && step.DecodeObserved,
            $"{expectation}: rejection did not occur after fetch/decode observation.");
        Require(!step.ExecuteObserved && !step.RetireObserved && step.Capture is null,
            $"{expectation}: rejection occurred after execution or retire side effects.");
        Require(report.RuntimeInstructionCount == 0 && report.RetirePublicationCount == 0,
            $"{expectation}: rejection reported runtime execution or architectural publication.");
        return 1;
    }

    private static ulong ExpectFullPipelineRetireRejection(
        HybridCpuCompiledProgram compiledProgram,
        Func<MatrixTileExecutionCaptureRecord, MatrixTileExecutionCaptureRecord> mutateCapture,
        string expectation)
    {
        ArgumentNullException.ThrowIfNull(mutateCapture);
        Processor.MainMemoryArea memory = CreateMemory(1);
        Processor.CPU_Core core = CreateCore(memory);
        byte[] source = [7, 8, 9, 10];
        WriteMemory(memory, LoadSourceBaseAddress, source);

        MatrixTileFullPipelineReport report =
            MatrixTileFullPipelineHarness.RunCompilerLoweredBundlesForTesting(
                ref core,
                NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundles),
                NativeTransportRuntimeAdapter.ToRuntime(compiledProgram.LoweredBundleAnnotations),
                new MatrixTileFullPipelineHarnessOptions
                {
                    ReplayAfterSuccessfulRetire = false,
                    MutateCaptureBeforeRetire = (step, capture) => mutateCapture(capture)
                });

        Require(report.Steps.Count == 1, $"{expectation}: expected one full pipeline evidence step.");
        MatrixTileFullPipelineStepEvidence step = report.Steps[0];
        Require(step.FetchObserved && step.DecodeObserved && step.ScheduleObserved && step.ExecuteObserved,
            $"{expectation}: invalid capture did not traverse fetch/decode/schedule/execute.");
        Require(step.FailClosedRejected && !step.RetireObserved,
            $"{expectation}: retire validation did not reject before publication.");
        Require(report.RuntimeInstructionCount == 1 && report.RetirePublicationCount == 0,
            $"{expectation}: retire rejection reported an architectural publication.");
        Require(!core.TryCaptureAnyMatrixTileSnapshot(0, 371, out _),
            $"{expectation}: failed load published partial tile state.");
        Require(ReadMemory(memory, StoreDestinationBaseAddress, source.Length).All(static value => value == 0),
            $"{expectation}: failed load changed unrelated destination memory.");
        return 1;
    }

    private static void AssertExpectedSidebands(
        InstructionsEnum opcode,
        InstructionSlotMetadata metadata,
        MatrixTileNumericPolicy? expectedNumericPolicy,
        MatrixTileLayoutPolicy? expectedLayoutPolicy,
        string surface)
    {
        Require(Nullable.Equals(metadata.MatrixTileNumericPolicy, expectedNumericPolicy),
            $"{opcode}: unexpected {surface} MatrixTileNumericPolicy sideband.");
        Require(Nullable.Equals(metadata.MatrixTileLayoutPolicy, expectedLayoutPolicy),
            $"{opcode}: unexpected {surface} MatrixTileLayoutPolicy sideband.");
    }

    private static MatrixTileExecutionCaptureRecord ExecuteAndGetCapture(
        ref Processor.CPU_Core core,
        MatrixTileMicroOp microOp)
    {
        Require(microOp.Execute(ref core), $"{microOp.OpCode} execute returned false.");
        return microOp.LastExecutionCapture
            ?? throw new InvalidOperationException($"{microOp.OpCode} did not produce an execution capture.");
    }

    private static void RetireAndReplay(
        ref Processor.CPU_Core core,
        MatrixTileMicroOp microOp,
        MatrixTileExecutionCaptureRecord capture,
        byte[] expectedPublishedImage,
        ushort destinationTileId,
        MatrixTileCanonicalDescriptorAbi descriptor,
        ref ScenarioCounters counters)
    {
        MatrixTileRetireOutcome retire = microOp.RetireCapturedResult(ref core, capture);
        Require(retire.IsSuccess, $"{microOp.OpCode} retire failed: {retire.Message}");
        RequireTile(ref core, destinationTileId, descriptor, expectedPublishedImage);
        counters.RetirePublicationCount++;

        MatrixTileReplayRollbackJournal journal = RequireJournal(microOp);
        MatrixTileRollbackOutcome rollback = microOp.RollbackRetiredResult(ref core, journal.ReplayIdentity);
        Require(rollback.Lifecycle == MatrixTileReplayRollbackLifecycle.RolledBack,
            $"{microOp.OpCode} rollback did not enter the rolled-back lifecycle.");
        MatrixTileRetireOutcome replay = microOp.ReplayRolledBackResult(ref core, journal.ReplayIdentity);
        Require(replay.IsSuccess, $"{microOp.OpCode} replay failed: {replay.Message}");
        RequireTile(ref core, destinationTileId, descriptor, expectedPublishedImage);
        counters.ReplayRoundTripCount++;
    }

    private static MatrixTileReplayRollbackJournal RequireJournal(MatrixTileMicroOp microOp) =>
        microOp.LastReplayRollbackJournal
        ?? throw new InvalidOperationException($"{microOp.OpCode} did not register a replay/rollback journal.");

    private static void ExpectRetireRejection(
        ref Processor.CPU_Core core,
        MatrixTileMicroOp microOp,
        MatrixTileExecutionCaptureRecord capture,
        string expectation)
    {
        try
        {
            _ = microOp.RetireCapturedResult(ref core, capture);
        }
        catch (MatrixTileRetireValidationException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected retire rejection for {expectation}.");
    }

    private static Processor.MainMemoryArea CreateMemory(ulong iterations)
    {
        ulong requiredLength = checked(StoreDestinationBaseAddress + (iterations * AddressStride) + AddressStride);
        Processor.MainMemoryArea memory = new Processor.MainMemoryArea();
        memory.SetLength(checked((int)Math.Max(requiredLength, 0x8000UL)));
        return memory;
    }

    private static Processor.CPU_Core CreateCore(Processor.MainMemoryArea memory, ushort coreId = 0) =>
        DiagnosticShadowFrontend.ConfigureNewCore(new Processor.CPU_Core(coreId, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Emulation)));

    private static void WriteMemory(Processor.MainMemoryArea memory, ulong address, byte[] data)
    {
        if (!memory.TryWritePhysicalRange(address, data))
        {
            throw new InvalidOperationException($"Unable to seed MatrixTile memory at 0x{address:X}.");
        }
    }

    private static byte[] ReadMemory(Processor.MainMemoryArea memory, ulong address, int count)
    {
        byte[] result = new byte[count];
        if (!memory.TryReadPhysicalRange(address, result))
        {
            throw new InvalidOperationException($"Unable to read MatrixTile memory at 0x{address:X}.");
        }

        return result;
    }

    private static void RequireTile(
        ref Processor.CPU_Core core,
        ushort tileId,
        MatrixTileCanonicalDescriptorAbi descriptor,
        byte[] expected)
    {
        Require(core.TryCaptureMatrixTileSnapshot(0, tileId, descriptor, out MatrixTileTileImage image),
            $"Expected tile {tileId} is not available with the canonical descriptor.");
        Require(image.Data.SequenceEqual(expected), $"Tile {tileId} does not match the expected packed image.");
    }

    private static byte[] CreatePayload(ulong iteration) =>
    [
        unchecked((byte)(1 + iteration)),
        unchecked((byte)(3 + (iteration * 3))),
        unchecked((byte)(5 + (iteration * 5))),
        unchecked((byte)(7 + (iteration * 7)))
    ];

    private static byte[] CreatePatternedPayload(int length, ulong iteration, int salt)
    {
        byte[] payload = new byte[length];
        Array.Fill(payload, (byte)0xA5);
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)(0x31 + (iteration * 7) + (ulong)(salt * 11) + (ulong)(index * 3)));
        }

        return payload;
    }

    private static byte[] CreateStridedMemoryPayload(
        byte[] compactPayload,
        ushort rows,
        ushort columns,
        uint rowStride)
    {
        int length = checked((int)(((rows - 1) * rowStride) + columns));
        byte[] payload = new byte[length];
        for (ushort row = 0; row < rows; row++)
        {
            for (ushort column = 0; column < columns; column++)
            {
                int compactOffset = checked((row * columns) + column);
                int stridedOffset = checked((int)((row * rowStride) + column));
                payload[stridedOffset] = compactPayload[compactOffset];
            }
        }

        return payload;
    }

    private static byte[] PackInt32(params int[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(int))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                values[index]);
        }

        return bytes;
    }

    private static byte[] PackInt16(params short[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(short))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short), sizeof(short)),
                values[index]);
        }

        return bytes;
    }

    private static byte[] PackUInt16(params ushort[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(ushort))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                values[index]);
        }

        return bytes;
    }

    private static byte[] PackUInt32(params uint[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(uint))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                values[index]);
        }

        return bytes;
    }

    private static byte[] PackInt64(params long[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(long))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                bytes.AsSpan(index * sizeof(long), sizeof(long)),
                values[index]);
        }

        return bytes;
    }

    private static byte[] PackUInt64(params ulong[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(ulong))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(index * sizeof(ulong), sizeof(ulong)),
                values[index]);
        }

        return bytes;
    }

    private static MatrixTileMaccSemanticContract CreateMaccContract(
        MatrixTileNumericProfileId profileId,
        ushort rows,
        ushort k,
        ushort columns)
    {
        MatrixTileNumericPolicy numericPolicy =
            MatrixTileNumericPolicyAbi.CreateSupportedPolicy(profileId);
        ushort elementSize = checked((ushort)
            MatrixTileNumericPolicyAbi.GetElementSizeBytes(numericPolicy.ElementType));
        ushort accumulatorElementSize = checked((ushort)
            MatrixTileNumericPolicyAbi.GetElementSizeBytes(numericPolicy.AccumulatorType));
        MatrixTileCanonicalDescriptorAbi left = MatrixTileCanonicalDescriptorAbi.Create(
            rows,
            k,
            elementSize,
            checked((uint)(k * elementSize)));
        MatrixTileCanonicalDescriptorAbi right = MatrixTileCanonicalDescriptorAbi.Create(
            k,
            columns,
            elementSize,
            checked((uint)(columns * elementSize)));
        MatrixTileCanonicalDescriptorAbi accumulator = MatrixTileCanonicalDescriptorAbi.Create(
            rows,
            columns,
            accumulatorElementSize,
            checked((uint)(columns * accumulatorElementSize)));

        return MatrixTileAccumulatorAndTransposePolicyAbi.CreateMaccContract(
            left,
            right,
            accumulator,
            numericPolicy,
            MatrixTileLayoutPolicyAbi.CreateMaccPolicy());
    }

    private static void VerifyMaccProfile(
        MatrixTileNumericProfileId profileId,
        byte[] leftPayload,
        byte[] rightPayload,
        byte[] accumulatorPayload,
        byte[] expectedPayload,
        ref ScenarioCounters counters)
    {
        MatrixTileMaccSemanticContract contract = CreateMaccContract(
            profileId,
            rows: 1,
            k: 1,
            columns: 1);
        Require(MatrixTileMaccArithmeticAbi.TryCompute(
                contract,
                MatrixTileTileImage.Create(1, contract.Left, leftPayload),
                MatrixTileTileImage.Create(2, contract.Right, rightPayload),
                MatrixTileTileImage.Create(3, contract.Accumulator, accumulatorPayload),
                out MatrixTileTileImage result,
                out MatrixTileMaccArithmeticFaultKind fault) &&
            fault == MatrixTileMaccArithmeticFaultKind.None &&
            result.Data.SequenceEqual(expectedPayload),
            $"{profileId} did not produce the expected byte-exact MACC result.");

        counters.ResultChecksum = MixChecksum(counters.ResultChecksum, Checksum(result.Data));
    }

    private static ulong MixGoldenCarrier(MatrixTileGoldenCarrier carrier)
    {
        ulong checksum = 1469598103934665603UL;
        checksum = MixChecksum(checksum, carrier.Word0);
        checksum = MixChecksum(checksum, carrier.Word1);
        checksum = MixChecksum(checksum, carrier.Word2);
        checksum = MixChecksum(checksum, carrier.Word3);
        return checksum;
    }

    private static void VerifyGoldenJsonMaccVector(JsonElement vector)
    {
        MatrixTileMaccSemanticContract contract = CreateGoldenMaccContract(vector);
        Require(MatrixTileMaccArithmeticAbi.TryCompute(
                contract,
                MatrixTileTileImage.Create(
                    RequiredUInt16(vector, "sourceTileId"),
                    contract.Left,
                    HexToBytes(RequiredString(vector, "sourceHex"))),
                MatrixTileTileImage.Create(
                    RequiredUInt16(vector, "secondaryTileId"),
                    contract.Right,
                    HexToBytes(RequiredString(vector, "secondaryHex"))),
                MatrixTileTileImage.Create(
                    RequiredUInt16(vector, "destinationTileId"),
                    contract.Accumulator,
                    HexToBytes(RequiredString(vector, "accumulatorBeforeHex"))),
                out MatrixTileTileImage result,
                out MatrixTileMaccArithmeticFaultKind fault) &&
            fault == MatrixTileMaccArithmeticFaultKind.None,
            $"{RequiredString(vector, "id")}: runtime MACC golden vector did not compute successfully.");
        Require(result.Data.SequenceEqual(HexToBytes(RequiredString(vector, "expectedStagedResultHex"))),
            $"{RequiredString(vector, "id")}: runtime MACC result drifted from golden JSON.");
        Require(RequiredString(vector, "expectedRetirePublication") == "Accumulator",
            $"{RequiredString(vector, "id")}: unexpected retire publication in MACC golden JSON.");
    }

    private static void VerifyGoldenJsonMaccFaultVector(JsonElement vector)
    {
        MatrixTileMaccSemanticContract contract = CreateGoldenMaccContract(vector);
        Require(!MatrixTileMaccArithmeticAbi.TryCompute(
                contract,
                MatrixTileTileImage.Create(
                    RequiredUInt16(vector, "sourceTileId"),
                    contract.Left,
                    HexToBytes(RequiredString(vector, "sourceHex"))),
                MatrixTileTileImage.Create(
                    RequiredUInt16(vector, "secondaryTileId"),
                    contract.Right,
                    HexToBytes(RequiredString(vector, "secondaryHex"))),
                MatrixTileTileImage.Create(
                    RequiredUInt16(vector, "destinationTileId"),
                    contract.Accumulator,
                    HexToBytes(RequiredString(vector, "accumulatorBeforeHex"))),
                out _,
                out MatrixTileMaccArithmeticFaultKind fault),
            $"{RequiredString(vector, "id")}: runtime MACC fault golden vector unexpectedly computed.");
        Require(fault.ToString() == RequiredString(vector, "expectedExecutionFault"),
            $"{RequiredString(vector, "id")}: runtime MACC fault kind drifted from golden JSON.");
        Require(RequiredString(vector, "expectedRetireFault") == MatrixTileRetireFaultKind.CapturedExecutionFault.ToString(),
            $"{RequiredString(vector, "id")}: retire fault expectation drifted from captured execution fault.");
    }

    private static void VerifyGoldenJsonTransposeVector(JsonElement vector)
    {
        MatrixTileCanonicalDescriptorAbi source = CreateGoldenDescriptor(vector);
        MatrixTileCanonicalDescriptorAbi destination = MatrixTileCanonicalDescriptorAbi.Create(
            source.Columns,
            source.Rows,
            source.ElementSizeBytes,
            checked((uint)(source.Rows * source.ElementSizeBytes)));
        MatrixTileTransposeSemanticContract contract =
            MatrixTileAccumulatorAndTransposePolicyAbi.CreateTransposeContract(
                source,
                destination,
                RequiredUInt16(vector, "sourceTileId"),
                RequiredUInt16(vector, "destinationTileId"),
                MatrixTileLayoutPolicyAbi.CreateTransposePolicy());
        Require(MatrixTileAccumulatorAndTransposePolicyAbi.ValidateRuntimeTranspose(contract).IsValid,
            $"{RequiredString(vector, "id")}: runtime transpose semantic validation failed.");
        byte[] transposed = TransposeCompactRowMajor(
            HexToBytes(RequiredString(vector, "sourceHex")),
            source.Rows,
            source.Columns,
            source.ElementSizeBytes);
        Require(transposed.SequenceEqual(HexToBytes(RequiredString(vector, "expectedStagedResultHex"))),
            $"{RequiredString(vector, "id")}: runtime transpose layout drifted from golden JSON.");
        Require(RequiredString(vector, "expectedRetirePublication") == "TileState",
            $"{RequiredString(vector, "id")}: unexpected retire publication in transpose golden JSON.");
    }

    private static void VerifyGoldenJsonProjectionFaultVector(JsonElement vector)
    {
        string operation = RequiredString(vector, "operation");
        if (operation == "MTRANSPOSE")
        {
            MatrixTileCanonicalDescriptorAbi source = CreateGoldenDescriptor(vector);
            MatrixTileTransposeSemanticContract contract =
                MatrixTileAccumulatorAndTransposePolicyAbi.CreateTransposeContract(
                    source,
                    MatrixTileCanonicalDescriptorAbi.Create(
                        source.Columns,
                        source.Rows,
                        source.ElementSizeBytes,
                        checked((uint)(source.Rows * source.ElementSizeBytes))),
                    RequiredUInt16(vector, "sourceTileId"),
                    RequiredUInt16(vector, "destinationTileId"),
                    MatrixTileLayoutPolicyAbi.CreateTransposePolicy());
            MatrixTileSemanticValidationResult result =
                MatrixTileAccumulatorAndTransposePolicyAbi.ValidateRuntimeTranspose(contract);
            Require(!result.IsValid &&
                    result.FaultKind.ToString() == RequiredString(vector, "expectedSemanticFault") &&
                    RequiredString(vector, "expectedProjectionFault") == MatrixTileIrProjectionFaultKind.TransposeSemanticFault.ToString(),
                $"{RequiredString(vector, "id")}: transpose projection-fault golden vector drifted from runtime semantics.");
            return;
        }

        if (operation == "MTILE_MACC")
        {
            DataTypeEnum dataType = ParseEnum<DataTypeEnum>(RequiredString(vector, "dataType"));
            MatrixTileNumericPolicy numericPolicy =
                MatrixTileNumericPolicyAbi.CreateSupportedPolicy(
                    ParseEnum<MatrixTileNumericProfileId>(RequiredString(vector, "numericProfile")));
            Require(numericPolicy.ElementType != dataType &&
                    RequiredString(vector, "expectedProjectionFault") == MatrixTileIrProjectionFaultKind.NumericPolicyFault.ToString() &&
                    RequiredString(vector, "expectedNumericPolicyFault") == MatrixTileNumericPolicyFaultKind.ContradictoryElementType.ToString(),
                $"{RequiredString(vector, "id")}: MACC projection-fault golden vector drifted from runtime numeric policy semantics.");
            return;
        }

        throw new InvalidOperationException($"{RequiredString(vector, "id")}: unsupported projectionFault operation {operation}.");
    }

    private static MatrixTileMaccSemanticContract CreateGoldenMaccContract(JsonElement vector)
    {
        MatrixTileNumericPolicy numericPolicy =
            MatrixTileNumericPolicyAbi.CreateSupportedPolicy(
                ParseEnum<MatrixTileNumericProfileId>(RequiredString(vector, "numericProfile")));
        MatrixTileCanonicalDescriptorAbi left = CreateGoldenDescriptor(vector);
        ushort accumulatorElementSize = checked((ushort)
            MatrixTileNumericPolicyAbi.GetElementSizeBytes(numericPolicy.AccumulatorType));
        MatrixTileCanonicalDescriptorAbi right = MatrixTileCanonicalDescriptorAbi.Create(
            left.Columns,
            RequiredUInt16(vector, "immediate"),
            left.ElementSizeBytes,
            checked((uint)(RequiredUInt16(vector, "immediate") * left.ElementSizeBytes)));
        MatrixTileCanonicalDescriptorAbi accumulator = MatrixTileCanonicalDescriptorAbi.Create(
            left.Rows,
            right.Columns,
            accumulatorElementSize,
            checked((uint)(right.Columns * accumulatorElementSize)));
        return MatrixTileAccumulatorAndTransposePolicyAbi.CreateMaccContract(
            left,
            right,
            accumulator,
            numericPolicy,
            MatrixTileLayoutPolicyAbi.CreateMaccPolicy());
    }

    private static MatrixTileCanonicalDescriptorAbi CreateGoldenDescriptor(JsonElement vector)
    {
        DataTypeEnum dataType = ParseEnum<DataTypeEnum>(RequiredString(vector, "dataType"));
        ushort columns = RequiredUInt16(vector, "immediate");
        ushort streamLength = RequiredUInt16(vector, "streamLength");
        Require(columns != 0 && streamLength % columns == 0,
            $"{RequiredString(vector, "id")}: invalid golden JSON shape.");
        ushort rows = checked((ushort)(streamLength / columns));
        ushort elementSize = checked((ushort)DataTypeUtils.SizeOf(dataType));
        return MatrixTileCanonicalDescriptorAbi.Create(
            rows,
            columns,
            elementSize,
            RequiredUInt32(vector, "rowStride"));
    }

    private static byte[] TransposeCompactRowMajor(
        byte[] source,
        ushort rows,
        ushort columns,
        ushort elementSize)
    {
        byte[] destination = new byte[source.Length];
        for (ushort row = 0; row < rows; row++)
        {
            for (ushort column = 0; column < columns; column++)
            {
                int sourceOffset = checked(((row * columns) + column) * elementSize);
                int destinationOffset = checked(((column * rows) + row) * elementSize);
                source.AsSpan(sourceOffset, elementSize).CopyTo(destination.AsSpan(destinationOffset, elementSize));
            }
        }

        return destination;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate repository file {relativePath}.");
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new InvalidOperationException($"Missing string property '{propertyName}'.");

    private static ushort RequiredUInt16(JsonElement element, string propertyName) =>
        checked((ushort)RequiredUInt32(element, propertyName));

    private static uint RequiredUInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetUInt32(out uint result)
            ? result
            : throw new InvalidOperationException($"Missing uint property '{propertyName}'.");

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out TEnum result)
            ? result
            : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} value '{value}'.");

    private static byte[] HexToBytes(string hex)
    {
        Require(hex.Length % 2 == 0, "Hex payload must contain an even number of characters.");
        byte[] bytes = new byte[hex.Length / 2];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static MatrixTileSpecScenarioReport Success(
        string id,
        string contour,
        string workloadShape,
        ulong iterations,
        ScenarioCounters counters,
        TimeSpan elapsed,
        IReadOnlyList<string> opcodes,
        IReadOnlyList<string> resourceEvidence)
    {
        double elapsedMs = Math.Max(elapsed.TotalMilliseconds, 0.000001d);
        double runtimeInstructionsPerMs = counters.RuntimeInstructionCount / elapsedMs;
        double streamBytesPerMs = counters.StreamBytesTransferred / elapsedMs;
        double retirePublicationsPerMs = counters.RetirePublicationCount / elapsedMs;
        double replayRoundTripsPerMs = counters.ReplayRoundTripCount / elapsedMs;

        return new(
            id,
            contour,
            workloadShape,
            iterations,
            counters.RuntimeInstructionCount,
            counters.CompilerEmissionCount,
            counters.RetirePublicationCount,
            counters.ReplayRoundTripCount,
            counters.FailClosedRejectionCount,
            counters.StreamBytesTransferred,
            counters.StreamInvalidationCount,
            runtimeInstructionsPerMs,
            streamBytesPerMs,
            retirePublicationsPerMs,
            replayRoundTripsPerMs,
            counters.RuntimeInstructionCount == 0 || runtimeInstructionsPerMs > 0,
            opcodes,
            resourceEvidence,
            elapsed.TotalMilliseconds,
            Passed: true,
            FailureMessage: string.Empty,
            counters.ResultChecksum);
    }

    private static MatrixTileSpecScenarioReport Failure(
        string id,
        string contour,
        ulong iterations,
        ScenarioCounters counters,
        TimeSpan elapsed,
        Exception exception) =>
        new(
            id,
            contour,
            "Runtime pressure scenario terminated before a complete validated result.",
            iterations,
            counters.RuntimeInstructionCount,
            counters.CompilerEmissionCount,
            counters.RetirePublicationCount,
            counters.ReplayRoundTripCount,
            counters.FailClosedRejectionCount,
            counters.StreamBytesTransferred,
            counters.StreamInvalidationCount,
            0,
            0,
            0,
            0,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            elapsed.TotalMilliseconds,
            Passed: false,
            FailureMessage: exception.ToString(),
            counters.ResultChecksum);

    private static MatrixTileSpecAggregate BuildAggregate(
        IReadOnlyList<MatrixTileSpecScenarioReport> scenarios)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (MatrixTileSpecScenarioReport scenario in scenarios)
        {
            checksum = MixChecksum(checksum, scenario.ResultChecksum);
        }

        double elapsedMs = Math.Max(scenarios.Sum(static scenario => scenario.ElapsedMilliseconds), 0.000001d);
        ulong runtimeInstructions = Sum(scenarios, static scenario => scenario.RuntimeInstructionCount);
        ulong streamBytes = Sum(scenarios, static scenario => scenario.StreamBytesTransferred);
        ulong retirePublications = Sum(scenarios, static scenario => scenario.RetirePublicationCount);
        ulong replayRoundTrips = Sum(scenarios, static scenario => scenario.ReplayRoundTripCount);

        return new MatrixTileSpecAggregate(
            ScenarioCount: scenarios.Count,
            PassedScenarioCount: scenarios.Count(static scenario => scenario.Passed),
            RuntimeInstructionCount: runtimeInstructions,
            CompilerEmissionCount: Sum(scenarios, static scenario => scenario.CompilerEmissionCount),
            RetirePublicationCount: retirePublications,
            ReplayRoundTripCount: replayRoundTrips,
            FailClosedRejectionCount: Sum(scenarios, static scenario => scenario.FailClosedRejectionCount),
            StreamBytesTransferred: streamBytes,
            StreamInvalidationCount: Sum(scenarios, static scenario => scenario.StreamInvalidationCount),
            RuntimeInstructionsPerMillisecond: runtimeInstructions / elapsedMs,
            StreamBytesPerMillisecond: streamBytes / elapsedMs,
            RetirePublicationsPerMillisecond: retirePublications / elapsedMs,
            ReplayRoundTripsPerMillisecond: replayRoundTrips / elapsedMs,
            MeetsSmokeThroughputBaseline: scenarios.All(static scenario => scenario.MeetsSmokeThroughputBaseline),
            ElapsedMilliseconds: elapsedMs,
            ResultChecksum: checksum);
    }

    private static ulong Sum(
        IEnumerable<MatrixTileSpecScenarioReport> scenarios,
        Func<MatrixTileSpecScenarioReport, ulong> selector)
    {
        ulong result = 0;
        foreach (MatrixTileSpecScenarioReport scenario in scenarios)
        {
            result = checked(result + selector(scenario));
        }

        return result;
    }

    private static ulong Checksum(ReadOnlySpan<byte> bytes)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (byte value in bytes)
        {
            checksum = MixChecksum(checksum, value);
        }

        return checksum;
    }

    private static ulong MixChecksum(ulong checksum, ulong value) =>
        unchecked((checksum ^ value) * 1_099_511_628_211UL);

    private static byte SelectLaneForClass(SlotClass slotClass) =>
        slotClass switch
        {
            SlotClass.AluClass => 0,
            SlotClass.LsuClass => 4,
            SlotClass.DmaStreamClass => MatrixTileResourceContour.TileStreamLaneId,
            SlotClass.MatrixTileStreamClass => MatrixTileResourceContour.TileStreamLaneId,
            SlotClass.BranchControl => 7,
            SlotClass.SystemSingleton => 7,
            _ => 0
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void BootstrapRuntime()
    {
#pragma warning disable CS0618
        _ = new Processor(ProcessorMode.Compiler);
#pragma warning restore CS0618
        DiagnosticShadowFrontend.ConfigureBootstrappedCores();

        Processor.CurrentProcessorMode = ProcessorMode.Emulation;
    }

    private sealed class ScenarioCounters
    {
        public ulong RuntimeInstructionCount { get; set; }

        public ulong CompilerEmissionCount { get; set; }

        public ulong RetirePublicationCount { get; set; }

        public ulong ReplayRoundTripCount { get; set; }

        public ulong FailClosedRejectionCount { get; set; }

        public ulong StreamBytesTransferred { get; set; }

        public ulong StreamInvalidationCount { get; set; }

        public ulong ResultChecksum { get; set; } = 14_695_981_039_346_656_037UL;
    }

    private sealed class FailSecondWriteMemory : Processor.MainMemoryArea
    {
        private bool _armed;
        private int _writeCount;

        public void Arm()
        {
            _armed = true;
            _writeCount = 0;
        }

        public override bool TryWritePhysicalRange(
            ulong physicalAddress,
            ReadOnlySpan<byte> buffer)
        {
            if (_armed && ++_writeCount == 2)
            {
                _armed = false;
                return false;
            }

            return base.TryWritePhysicalRange(physicalAddress, buffer);
        }
    }

    private sealed class TestOnlySlotClassClaimMicroOp : MicroOp
    {
        private readonly string _description;

        public TestOnlySlotClassClaimMicroOp(
            SlotClass slotClass,
            byte laneId,
            string description)
        {
            _description = description;
            SetHardPinnedPlacement(slotClass, laneId);
            RefreshAdmissionMetadata();
        }

        public override bool Execute(ref Processor.CPU_Core core) => true;

        public override string GetDescription() => _description;
    }
}
