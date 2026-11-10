using HybridCPU.Compiler.Core.IR;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using HybridCPU.Compiler.Core.Support;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU.Compiler.Core.Runtime;
using System.Text;

namespace HybridCPU.Compiler.Core.Multithreaded
{
    /// <summary>
    /// Phase 4 SMT Extension: Configuration for multithreaded compilation.
    /// </summary>
    public class HybridCpuMultithreadedCompilerConfig
    {
        /// <summary>
        /// Enable automatic partitioning of single-threaded code across VTs.
        /// </summary>
        public bool EnableAutoPartitioning { get; set; } = false;

        /// <summary>
        /// Partitioning strategy when auto-partitioning is enabled.
        /// </summary>
        public PartitioningStrategy Strategy { get; set; } = PartitioningStrategy.Manual;

        /// <summary>
        /// Per-VT security domain tags (0 = no isolation).
        /// Enforced by MEM_DOMAIN_CERT CSR at runtime.
        /// </summary>
        public ulong[] ThreadDomainTags { get; set; } = new ulong[4];

        /// <summary>
        /// Enable FSP slot stealing for non-critical operations.
        /// </summary>
        public bool EnableSlotStealing { get; set; } = true;

        /// <summary>
        /// Maximum threads per VLIW bundle (1-4 for 4-way SMT).
        /// </summary>
        public byte MaxThreadsPerBundle { get; set; } = 4;
    }

    /// <summary>
    /// Auto-partitioning strategies for distributing single-threaded code across VTs.
    /// </summary>
    public enum PartitioningStrategy
    {
        Manual,            // Explicit .thread directives (no auto-partition)
        LoopStripMining,   // Divide loop iterations evenly across VTs
        BasicBlockLevel,   // Parallel independent basic blocks
        FunctionLevel      // Map-reduce style function call parallelism
    }

    /// <summary>
    /// Canonical multithreaded compilation artifacts.
    /// This is the authoritative result contract for VT-local Stage 5/6 outputs.
    /// </summary>
    public class HybridCpuMultithreadedCompilationArtifacts
    {
        /// <summary>
        /// Per-VT domain tags embedded in program metadata.
        /// </summary>
        public ulong[] ThreadDomainTags { get; set; } = new ulong[4];

        /// <summary>
        /// Canonical Stage 5/6 compilation artifacts for each populated VT.
        /// Empty VT slots remain null to preserve sparse multithreaded inputs.
        /// </summary>
        public HybridCpuCompiledProgram?[] CanonicalThreadPrograms { get; set; } = new HybridCpuCompiledProgram?[4];

        /// <summary>
        /// Total instructions compiled (across all VTs).
        /// </summary>
        public int TotalInstructions { get; set; }

        /// <summary>
        /// Total VLIW bundles generated.
        /// </summary>
        public int TotalBundles { get; set; }

        /// <summary>
        /// Compilation statistics.
        /// </summary>
        public HybridCpuCompilationStatistics Stats { get; set; } = new HybridCpuCompilationStatistics();

        /// <summary>
        /// Builds materialized Stage 6 bundle layouts for all populated VT-local canonical artifacts already present in the result surface.
        /// </summary>
        public IrProgramBundlingResult?[] BuildBundleLayouts()
        {
            IrProgramBundlingResult?[] bundleLayouts = new IrProgramBundlingResult?[CanonicalThreadPrograms.Length];
            for (int vt = 0; vt < CanonicalThreadPrograms.Length; vt++)
            {
                if (CanonicalThreadPrograms[vt] is not null)
                {
                    bundleLayouts[vt] = CanonicalThreadPrograms[vt]!.BundleLayout;
                }
            }

            return bundleLayouts;
        }

        /// <summary>
        /// Builds backend-facing `VLIW_Bundle` instances for all populated VT-local canonical artifacts already present in the result surface.
        /// </summary>
        public IReadOnlyList<VLIW_Bundle>?[] BuildVliwBundles()
        {
            IReadOnlyList<VLIW_Bundle>?[] loweredBundles = new IReadOnlyList<VLIW_Bundle>?[CanonicalThreadPrograms.Length];
            for (int vt = 0; vt < CanonicalThreadPrograms.Length; vt++)
            {
                if (CanonicalThreadPrograms[vt] is not null)
                {
                    loweredBundles[vt] = CanonicalThreadPrograms[vt]!.LoweredBundles
                        .Select(static bundle => NativeTransportRuntimeAdapter.ToRuntime(bundle))
                        .ToArray();
                }
            }

            return loweredBundles;
        }

        /// <summary>
        /// Builds fetch-ready bundle images for all populated VT-local canonical artifacts already present in the result surface.
        /// </summary>
        public byte[]?[] BuildVliwBundleImages()
        {
            byte[]?[] programImages = new byte[]?[CanonicalThreadPrograms.Length];
            for (int vt = 0; vt < CanonicalThreadPrograms.Length; vt++)
            {
                if (CanonicalThreadPrograms[vt] is not null)
                {
                    programImages[vt] = CanonicalThreadPrograms[vt]!.ProgramImage;
                }
            }

            return programImages;
        }

        /// <summary>
        /// Emits the already materialized Stage 6 bundle images for all populated VT-local canonical artifacts into main memory at the specified per-VT base addresses.
        /// </summary>
        public HybridCpuCompiledProgram?[] EmitVliwBundleImages(ulong[] emissionBaseAddresses)
        {
            ArgumentNullException.ThrowIfNull(emissionBaseAddresses);
            if (emissionBaseAddresses.Length != CanonicalThreadPrograms.Length)
            {
                throw new ArgumentException($"Expected {CanonicalThreadPrograms.Length} emission base addresses (one per VT).", nameof(emissionBaseAddresses));
            }

            HybridCpuCompiledProgram?[] emittedPrograms = new HybridCpuCompiledProgram?[CanonicalThreadPrograms.Length];
            for (int vt = 0; vt < CanonicalThreadPrograms.Length; vt++)
            {
                if (CanonicalThreadPrograms[vt] is null)
                {
                    continue;
                }

                emittedPrograms[vt] = NativeTransportRuntimeAdapter.EmitProgram(
                    CanonicalThreadPrograms[vt]!, emissionBaseAddresses[vt]);
                CanonicalThreadPrograms[vt] = emittedPrograms[vt];
            }

            return emittedPrograms;
        }
    }

    /// <summary>
    /// Compatibility wrapper around the canonical multithreaded artifact set.
    /// Retains the older round-robin interleaved binary surface for legacy callers.
    /// </summary>
    public class HybridCpuMultithreadedCompiledProgram : HybridCpuMultithreadedCompilationArtifacts
    {
        /// <summary>
        /// Compatibility-only round-robin interleaved instruction image.
        /// Prefer <see cref="CanonicalThreadPrograms"/> for authoritative VT-local outputs.
        /// </summary>
        public byte[] BinaryCode { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Compatibility-only per-VT offsets into <see cref="BinaryCode"/>.
        /// Prefer canonical VT-local images and explicit emission addresses.
        /// </summary>
        public int[] ThreadEntryPoints { get; set; } = new int[4];
    }

    /// <summary>
    /// Compilation statistics for performance analysis.
    /// </summary>
    public class HybridCpuCompilationStatistics
    {
        /// <summary>
        /// Maximum instructions across all VTs.
        /// </summary>
        public int InstructionsPerThread { get; set; }

        /// <summary>
        /// Number of barriers inserted for synchronization.
        /// </summary>
        public int BarriersInserted { get; set; }

        /// <summary>
        /// Number of cross-thread dependencies detected.
        /// </summary>
        public int CrossThreadDependencies { get; set; }

        /// <summary>
        /// Average slots filled per VLIW bundle (utilization metric).
        /// </summary>
        public double BundleUtilization { get; set; }
    }

    /// <summary>
    /// Phase 4 SMT Extension: Main multithreaded compiler for 4-way SMT.
    /// Manages 4 HybridCpuThreadCompilerContexts and coordinates cross-thread compilation.
    /// </summary>
    public partial class HybridCpuMultithreadedCompiler
    {
        private const ushort FullPodAffinityMask = 0b1111;
        private readonly HybridCpuThreadCompilerContext[] _threadContexts = new HybridCpuThreadCompilerContext[4];
        private readonly HybridCpuDependencyGraph _depGraph = new HybridCpuDependencyGraph();
        private readonly HybridCpuBarrierScheduler _barrierScheduler = new HybridCpuBarrierScheduler();

        /// <summary>
        /// Creates a new multithreaded compiler.
        /// </summary>
        public HybridCpuMultithreadedCompiler()
        {
            // Initialize per-VT compiler contexts
            for (byte vt = 0; vt < 4; vt++)
            {
                _threadContexts[vt] = new HybridCpuThreadCompilerContext(vt);
            }
        }

        /// <summary>
        /// Gets the compiler context for a specific virtual thread.
        /// </summary>
        public HybridCpuThreadCompilerContext GetThreadContext(byte vtId)
        {
            if (vtId > 3)
                throw new ArgumentOutOfRangeException(nameof(vtId));

            return _threadContexts[vtId];
        }

        /// <summary>
        /// Compiles a multithreaded program through the canonical VT-local Stage 5/6 pipeline.
        /// This is the authoritative API for explicit multithreaded compilation.
        /// </summary>
        /// <param name="config">Compilation configuration</param>
        /// <returns>Canonical multithreaded artifacts keyed by virtual thread</returns>
        public HybridCpuMultithreadedCompilationArtifacts CompileCanonicalMultithreaded(HybridCpuMultithreadedCompilerConfig config)
        {
            return CompileCanonicalMultithreadedCore(config, out _);
        }

        /// <summary>
        /// Compatibility wrapper that preserves the older round-robin interleaved binary output.
        /// Prefer <see cref="CompileCanonicalMultithreaded"/> for the canonical artifact contract.
        /// </summary>
        /// <param name="config">Compilation configuration</param>
        /// <returns>Canonical artifacts plus legacy interleaved binary compatibility outputs</returns>
        public HybridCpuMultithreadedCompiledProgram CompileMultithreaded(HybridCpuMultithreadedCompilerConfig config)
        {
            HybridCpuMultithreadedCompilationArtifacts canonicalArtifacts = CompileCanonicalMultithreadedCore(config, out HybridCpuThreadCompilerContext[] compilationContexts);
            return BuildCompatibilityCompiledProgram(canonicalArtifacts, compilationContexts);
        }

        /// <summary>
        /// Records a memory access for dependency analysis.
        /// Must be called during instruction compilation to track cross-thread hazards.
        /// </summary>
        public void RecordMemoryAccess(byte vtId, ulong address, uint length, bool isWrite, int instructionIndex = 0)
        {
            _depGraph.RecordMemoryAccess(vtId, address, length, isWrite, instructionIndex);
        }

        /// <summary>
        /// Inserts a manual barrier directive at a specific point in a VT's instruction stream.
        /// </summary>
        public void InsertBarrier(byte vtId, int instructionIndex, byte[] participatingThreads)
        {
            _barrierScheduler.AddManualBarrier(vtId, instructionIndex, participatingThreads);
        }

        /// <summary>
        /// Resets all per-VT compiler contexts and dependency tracking.
        /// Prepares for a new compilation.
        /// </summary>
        public void Reset()
        {
            for (byte vt = 0; vt < 4; vt++)
            {
                _threadContexts[vt].Reset();
            }
            _depGraph.Reset();
            _barrierScheduler.Reset();
        }

        /// <summary>
        /// Returns a human-readable compilation report.
        /// </summary>
        public string GenerateCompilationReport()
        {
            StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Multithreaded Compilation Report ===");
            sb.AppendLine();

            sb.AppendLine("Per-VT Instruction Counts:");
            for (byte vt = 0; vt < 4; vt++)
            {
                sb.AppendLine($"  VT-{vt}: {_threadContexts[vt].InstructionCount} instructions, Domain Tag: 0x{_threadContexts[vt].DomainTag:X16}");
            }
            sb.AppendLine();

            sb.AppendLine(_depGraph.DependencyMatrixToString());
            sb.AppendLine();

            sb.AppendLine(_barrierScheduler.BarriersToString());

            return sb.ToString();
        }

        private HybridCpuThreadCompilerContext[] CreateCompilationContexts()
        {
            HybridCpuThreadCompilerContext[] compilationContexts = new HybridCpuThreadCompilerContext[_threadContexts.Length];
            for (int vt = 0; vt < _threadContexts.Length; vt++)
            {
                compilationContexts[vt] = _threadContexts[vt].CreateDetachedCopy();
            }

            return compilationContexts;
        }

        private HybridCpuMultithreadedCompilationArtifacts CompileCanonicalMultithreadedCore(
            HybridCpuMultithreadedCompilerConfig config,
            out HybridCpuThreadCompilerContext[] compilationContexts)
        {
            ValidateConfig(config);
            ApplyThreadDomainTags(config.ThreadDomainTags);
            AnalyzeDependenciesOrThrow();
            PrepareBarrierSchedule();

            compilationContexts = CreateCompilationContexts();
            MaterializeScheduledBarriers(compilationContexts);

            HybridCpuCompiledProgram?[] canonicalPrograms = CompileCanonicalThreadPrograms(compilationContexts);
            int totalInstructions = GetTotalInstructionCount(compilationContexts);

            return new HybridCpuMultithreadedCompilationArtifacts
            {
                ThreadDomainTags = (ulong[])config.ThreadDomainTags.Clone(),
                CanonicalThreadPrograms = canonicalPrograms,
                TotalInstructions = totalInstructions,
                TotalBundles = GetCanonicalBundleCount(canonicalPrograms),
                Stats = CreateCompilationStatistics(compilationContexts, canonicalPrograms, totalInstructions)
            };
        }

        private void ValidateConfig(HybridCpuMultithreadedCompilerConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(config.ThreadDomainTags);
            if (config.ThreadDomainTags.Length != _threadContexts.Length)
            {
                throw new ArgumentException(
                    $"Expected {_threadContexts.Length} thread domain tags (one per VT).",
                    nameof(config.ThreadDomainTags));
            }
        }

        private void ApplyThreadDomainTags(ulong[] threadDomainTags)
        {
            for (int vt = 0; vt < _threadContexts.Length; vt++)
            {
                _threadContexts[vt].DomainTag = threadDomainTags[vt];
            }
        }

        private void AnalyzeDependenciesOrThrow()
        {
            // Memory accesses must be recorded via RecordMemoryAccess before compilation
            // so the barrier scheduler sees the final cross-thread hazard graph.
            _depGraph.AnalyzeDependencies();
            if (_depGraph.HasCyclicDependency())
            {
                throw new InvalidOperationException("Cyclic dependency detected between VTs (potential deadlock)");
            }
        }

        private void PrepareBarrierSchedule()
        {
            _barrierScheduler.InsertBarriers(_depGraph);
            _barrierScheduler.OptimizeBarriers();
        }

        private HybridCpuCompilationStatistics CreateCompilationStatistics(
            HybridCpuThreadCompilerContext[] compilationContexts,
            HybridCpuCompiledProgram?[] canonicalPrograms,
            int totalInstructions)
        {
            int maxInstructions = 0;
            for (int vt = 0; vt < compilationContexts.Length; vt++)
            {
                if (compilationContexts[vt].InstructionCount > maxInstructions)
                {
                    maxInstructions = compilationContexts[vt].InstructionCount;
                }
            }

            return new HybridCpuCompilationStatistics
            {
                InstructionsPerThread = maxInstructions,
                BarriersInserted = _barrierScheduler.BarrierCount,
                CrossThreadDependencies = _depGraph.GetDependencyCount(),
                BundleUtilization = GetCanonicalBundleUtilization(canonicalPrograms, totalInstructions)
            };
        }

        private static HybridCpuMultithreadedCompiledProgram BuildCompatibilityCompiledProgram(
            HybridCpuMultithreadedCompilationArtifacts canonicalArtifacts,
            HybridCpuThreadCompilerContext[] compilationContexts)
        {
            ArgumentNullException.ThrowIfNull(canonicalArtifacts);
            ArgumentNullException.ThrowIfNull(compilationContexts);

            (byte[] binaryCode, int[] threadEntryPoints) = BuildCompatibilityInterleavedBinary(compilationContexts);

            return new HybridCpuMultithreadedCompiledProgram
            {
                ThreadDomainTags = (ulong[])canonicalArtifacts.ThreadDomainTags.Clone(),
                CanonicalThreadPrograms = (HybridCpuCompiledProgram?[])canonicalArtifacts.CanonicalThreadPrograms.Clone(),
                TotalInstructions = canonicalArtifacts.TotalInstructions,
                TotalBundles = canonicalArtifacts.TotalBundles,
                Stats = CloneStatistics(canonicalArtifacts.Stats),
                BinaryCode = binaryCode,
                ThreadEntryPoints = threadEntryPoints
            };
        }

        private static (byte[] BinaryCode, int[] ThreadEntryPoints) BuildCompatibilityInterleavedBinary(
            HybridCpuThreadCompilerContext[] compilationContexts)
        {
            int totalInstructions = GetTotalInstructionCount(compilationContexts);
            byte[] binaryCode = new byte[totalInstructions * 32];
            int[] threadEntryPoints = new int[compilationContexts.Length];
            Array.Fill(threadEntryPoints, -1);

            int writeOffset = 0;
            int maxInstructions = GetMaxInstructionCount(compilationContexts);
            for (int instructionIndex = 0; instructionIndex < maxInstructions; instructionIndex++)
            {
                for (int vt = 0; vt < compilationContexts.Length; vt++)
                {
                    ReadOnlySpan<VLIW_Instruction> instructions = compilationContexts[vt].GetCompiledInstructions();
                    if (instructionIndex >= instructions.Length)
                    {
                        continue;
                    }

                    if (threadEntryPoints[vt] < 0)
                    {
                        threadEntryPoints[vt] = writeOffset;
                    }

                    instructions[instructionIndex].TryWriteBytes(binaryCode.AsSpan(writeOffset, 32));
                    writeOffset += 32;
                }
            }

            return (binaryCode, threadEntryPoints);
        }

        private static int GetTotalInstructionCount(HybridCpuThreadCompilerContext[] compilationContexts)
        {
            int totalInstructions = 0;
            for (int vt = 0; vt < compilationContexts.Length; vt++)
            {
                totalInstructions += compilationContexts[vt].InstructionCount;
            }

            return totalInstructions;
        }

        private static int GetMaxInstructionCount(HybridCpuThreadCompilerContext[] compilationContexts)
        {
            int maxInstructions = 0;
            for (int vt = 0; vt < compilationContexts.Length; vt++)
            {
                if (compilationContexts[vt].InstructionCount > maxInstructions)
                {
                    maxInstructions = compilationContexts[vt].InstructionCount;
                }
            }

            return maxInstructions;
        }

        private static HybridCpuCompilationStatistics CloneStatistics(HybridCpuCompilationStatistics stats)
        {
            ArgumentNullException.ThrowIfNull(stats);
            return new HybridCpuCompilationStatistics
            {
                InstructionsPerThread = stats.InstructionsPerThread,
                BarriersInserted = stats.BarriersInserted,
                CrossThreadDependencies = stats.CrossThreadDependencies,
                BundleUtilization = stats.BundleUtilization
            };
        }

        private void MaterializeScheduledBarriers(HybridCpuThreadCompilerContext[] threadContexts)
        {
            ReadOnlySpan<HybridCpuBarrierScheduler.BarrierPoint> barriers = _barrierScheduler.GetBarriers();
            for (int barrierIndex = barriers.Length - 1; barrierIndex >= 0; barrierIndex--)
            {
                HybridCpuBarrierScheduler.BarrierPoint barrier = barriers[barrierIndex];
                HybridCpuThreadCompilerContext context = threadContexts[barrier.VirtualThreadId];

                uint barrierOpcode = barrier.AffinityMask == FullPodAffinityMask
                    ? (uint)InstructionsEnum.POD_BARRIER
                    : (uint)InstructionsEnum.VT_BARRIER;
                ushort immediate = barrierOpcode == (uint)InstructionsEnum.VT_BARRIER
                    ? barrier.AffinityMask
                    : (ushort)0;

                context.InsertInstruction(
                    barrier.InstructionIndex,
                    opCode: barrierOpcode,
                    dataType: 0,
                    predicate: 0xFF,
                    immediate: immediate,
                    destSrc1: 0,
                    src2: 0,
                    streamLength: 0,
                    stride: 0,
                    stealabilityPolicy: YAKSys_Hybrid_CPU.Core.StealabilityPolicy.NotStealable);
            }
        }
    }
}
