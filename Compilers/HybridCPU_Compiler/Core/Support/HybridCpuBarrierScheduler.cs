using System.Text;

namespace HybridCPU.Compiler.Core.Support
{
    /// <summary>
    /// Phase 4 SMT Extension: Barrier insertion and synchronization point scheduler.
    /// Inserts WAIT_FOR_CLUSTER_SYNC barriers at points where cross-thread dependencies must be resolved.
    /// Generates POD_AFFINITY_MASK bitmasks for thread participation.
    /// </summary>
    public class HybridCpuBarrierScheduler
    {
        /// <summary>
        /// Represents a barrier insertion point in a per-VT instruction stream.
        /// </summary>
        public struct BarrierPoint
        {
            public byte VirtualThreadId;      // VT where barrier should be inserted
            public int InstructionIndex;      // Position in VT's instruction stream (insert before)
            public ushort AffinityMask;       // POD_AFFINITY_MASK CSR value (which VTs participate)
            public byte[] ParticipatingThreads; // Array of VT IDs that must synchronize
            public bool IsManual;             // True when requested explicitly by the caller
        }

        // HLS-compatible: fixed-size barrier list
        private const int MAX_BARRIERS = 32;
        private readonly BarrierPoint[] _barriers = new BarrierPoint[MAX_BARRIERS];
        private int _barrierCount = 0;

        /// <summary>
        /// Analyzes a dependency graph and determines where barriers are needed.
        /// Inserts barriers at:
        /// - Producer VT: after last write before dependent read
        /// - Consumer VT: before first read that depends on producer write
        /// </summary>
        /// <param name="dependencyGraph">Cross-thread dependency analysis</param>
        /// <returns>Array of barrier points to insert</returns>
        public BarrierPoint[] InsertBarriers(HybridCpuDependencyGraph dependencyGraph)
        {
            RemoveAutomaticBarriers();

            // For each VT, check if it depends on other VTs
            for (byte vt = 0; vt < 4; vt++)
            {
                byte[] dependencies = dependencyGraph.GetDependencies(vt);

                if (dependencies.Length > 0)
                {
                    // VT has dependencies → insert barrier before VT starts consuming
                    // All producer threads (dependencies) must complete before this VT proceeds

                    byte[] participatingThreads = new byte[dependencies.Length + 1];
                    Array.Copy(dependencies, participatingThreads, dependencies.Length);
                    participatingThreads[dependencies.Length] = vt; // Include consumer in barrier

                    BarrierPoint barrierPoint = new BarrierPoint
                    {
                        VirtualThreadId = vt,
                        InstructionIndex = 0, // Insert at beginning of VT stream (before consumption)
                        AffinityMask = GenerateAffinityMask(participatingThreads),
                        ParticipatingThreads = participatingThreads,
                        IsManual = false
                    };

                    if (_barrierCount < MAX_BARRIERS)
                    {
                        _barriers[_barrierCount] = barrierPoint;
                        _barrierCount++;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Barrier limit exceeded (max {MAX_BARRIERS})");
                    }
                }
            }

            // Return array of barriers to insert
            BarrierPoint[] result = new BarrierPoint[_barrierCount];
            Array.Copy(_barriers, result, _barrierCount);
            return result;
        }

        /// <summary>
        /// Generates a POD_AFFINITY_MASK CSR value from participating thread IDs.
        /// Each bit represents a VT: bit 0 = VT-0, bit 1 = VT-1, etc.
        /// Example: [0, 2] → 0b0101 = 5
        /// </summary>
        /// <param name="participatingThreads">Array of VT IDs (0-3)</param>
        /// <returns>Bitmask for POD_AFFINITY_MASK CSR</returns>
        public ushort GenerateAffinityMask(byte[] participatingThreads)
        {
            ushort mask = 0;

            foreach (byte vtId in participatingThreads)
            {
                if (vtId > 3)
                    throw new ArgumentOutOfRangeException(nameof(participatingThreads), $"VT ID {vtId} out of range (must be 0-3)");

                mask |= (ushort)(1 << vtId);
            }

            return mask;
        }

        /// <summary>
        /// Optimizes barrier placement by coalescing adjacent barriers with identical affinity masks.
        /// Reduces synchronization overhead when multiple barriers can be merged.
        /// </summary>
        public void OptimizeBarriers()
        {
            if (_barrierCount <= 1)
                return;

            // Sort barriers by VT ID and instruction index for coalescing
            // Simple bubble sort (O(n²) but n ≤ 32 for barriers)
            for (int i = 0; i < _barrierCount - 1; i++)
            {
                for (int j = 0; j < _barrierCount - i - 1; j++)
                {
                    BarrierPoint b1 = _barriers[j];
                    BarrierPoint b2 = _barriers[j + 1];

                    if (b1.VirtualThreadId > b2.VirtualThreadId ||
                        (b1.VirtualThreadId == b2.VirtualThreadId && b1.InstructionIndex > b2.InstructionIndex))
                    {
                        _barriers[j] = b2;
                        _barriers[j + 1] = b1;
                    }
                }
            }

            // Coalesce adjacent barriers with same affinity mask
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < _barrierCount; readIndex++)
            {
                BarrierPoint current = _barriers[readIndex];

                // Check if we can merge with previous barrier
                if (writeIndex > 0)
                {
                    BarrierPoint previous = _barriers[writeIndex - 1];

                    if (previous.VirtualThreadId == current.VirtualThreadId &&
                        previous.AffinityMask == current.AffinityMask &&
                        previous.IsManual == current.IsManual &&
                        (current.InstructionIndex - previous.InstructionIndex) <= 5) // Within 5 instructions
                    {
                        // Merge: keep earlier barrier, discard current
                        continue;
                    }
                }

                _barriers[writeIndex] = current;
                writeIndex++;
            }

            _barrierCount = writeIndex;
        }

        /// <summary>
        /// Inserts a manual barrier at a specific point in a VT's instruction stream.
        /// Used for explicit synchronization directives (e.g., BARRIER directive in ASM).
        /// </summary>
        public void AddManualBarrier(byte vtId, int instructionIndex, byte[] participatingThreads)
        {
            if (vtId > 3)
                throw new ArgumentOutOfRangeException(nameof(vtId));

            if (_barrierCount >= MAX_BARRIERS)
                throw new InvalidOperationException($"Barrier limit exceeded (max {MAX_BARRIERS})");

            _barriers[_barrierCount] = new BarrierPoint
            {
                VirtualThreadId = vtId,
                InstructionIndex = instructionIndex,
                AffinityMask = GenerateAffinityMask(participatingThreads),
                ParticipatingThreads = participatingThreads,
                IsManual = true
            };
            _barrierCount++;
        }

        /// <summary>
        /// Returns all currently scheduled barriers.
        /// </summary>
        public ReadOnlySpan<BarrierPoint> GetBarriers()
        {
            return new ReadOnlySpan<BarrierPoint>(_barriers, 0, _barrierCount);
        }

        /// <summary>
        /// Returns the number of barriers currently scheduled.
        /// </summary>
        public int BarrierCount => _barrierCount;

        /// <summary>
        /// Resets the barrier scheduler (clears all scheduled barriers).
        /// </summary>
        public void Reset()
        {
            _barrierCount = 0;
        }

        private void RemoveAutomaticBarriers()
        {
            if (_barrierCount == 0)
            {
                return;
            }

            int writeIndex = 0;
            for (int readIndex = 0; readIndex < _barrierCount; readIndex++)
            {
                BarrierPoint barrier = _barriers[readIndex];
                if (!barrier.IsManual)
                {
                    continue;
                }

                _barriers[writeIndex] = barrier;
                writeIndex++;
            }

            _barrierCount = writeIndex;
        }

        /// <summary>
        /// Returns a human-readable description of all scheduled barriers.
        /// Useful for debugging and visualization.
        /// </summary>
        public string BarriersToString()
        {
            StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"Scheduled Barriers ({_barrierCount}):");

            for (int i = 0; i < _barrierCount; i++)
            {
                BarrierPoint barrier = _barriers[i];
                sb.Append($"  [{i}] VT-{barrier.VirtualThreadId} @ instruction {barrier.InstructionIndex}: ");
                sb.Append($"{(barrier.IsManual ? "manual" : "auto")} affinity=0x{barrier.AffinityMask:X4} (");

                for (int bit = 0; bit < 4; bit++)
                {
                    if ((barrier.AffinityMask & (1 << bit)) != 0)
                        sb.Append($"VT-{bit} ");
                }

                sb.AppendLine(")");
            }

            return sb.ToString();
        }
    }
}
