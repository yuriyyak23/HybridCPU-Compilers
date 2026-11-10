using System.Text;

namespace HybridCPU.Compiler.Core.Support
{
    /// <summary>
    /// Phase 4 SMT Extension: Cross-thread dependency tracking for multithreaded compilation.
    /// Models happens-before relationships between instructions on different virtual threads.
    /// Detects RAW (Read-After-Write), WAW (Write-After-Write), and WAR (Write-After-Read) hazards.
    /// </summary>
    public class HybridCpuDependencyGraph
    {
        // HLS-compatible: fixed-size dependency matrix for 4-way SMT
        // dependsOn[i,j] = true means VT-i has unresolved dependency on VT-j
        private readonly bool[,] _dependsOn = new bool[4, 4];

        // Memory access tracking for dependency analysis
        private struct MemoryAccess
        {
            public byte VirtualThreadId;    // VT that performed the access
            public ulong Address;           // Memory address accessed
            public uint Length;             // Access length in bytes
            public bool IsWrite;            // true = write, false = read
            public int InstructionIndex;    // Instruction index within VT's stream
        }

        // HLS-compatible: fixed-size memory access log
        private const int MAX_MEMORY_ACCESSES = 128;
        private readonly MemoryAccess[] _memoryAccesses = new MemoryAccess[MAX_MEMORY_ACCESSES];
        private int _memoryAccessCount = 0;

        /// <summary>
        /// Records a memory access by a virtual thread.
        /// Used to build the dependency graph via AnalyzeDependencies().
        /// </summary>
        /// <param name="vtId">Virtual thread ID (0-3)</param>
        /// <param name="address">Memory address</param>
        /// <param name="length">Access length in bytes</param>
        /// <param name="isWrite">True for write, false for read</param>
        /// <param name="instructionIndex">Instruction index in VT's stream (for ordering)</param>
        public void RecordMemoryAccess(byte vtId, ulong address, uint length, bool isWrite, int instructionIndex = 0)
        {
            if (vtId > 3)
                throw new ArgumentOutOfRangeException(nameof(vtId), "VirtualThreadId must be 0-3");

            if (_memoryAccessCount >= MAX_MEMORY_ACCESSES)
                throw new InvalidOperationException($"Memory access log overflow (max {MAX_MEMORY_ACCESSES} accesses)");

            _memoryAccesses[_memoryAccessCount] = new MemoryAccess
            {
                VirtualThreadId = vtId,
                Address = address,
                Length = length,
                IsWrite = isWrite,
                InstructionIndex = instructionIndex
            };
            _memoryAccessCount++;
        }

        /// <summary>
        /// Analyzes all recorded memory accesses and populates the dependency matrix.
        /// Detects:
        /// - RAW (Read-After-Write): VT-i reads from address that VT-j writes
        /// - WAW (Write-After-Write): VT-i and VT-j both write to same address
        /// - WAR (Write-After-Read): VT-i writes to address that VT-j reads (anti-dependency)
        /// </summary>
        public void AnalyzeDependencies()
        {
            // Clear existing dependencies
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    _dependsOn[i, j] = false;
                }
            }

            // Pairwise comparison of all memory accesses (O(n²) but n ≤ 128)
            for (int i = 0; i < _memoryAccessCount; i++)
            {
                for (int j = i + 1; j < _memoryAccessCount; j++)
                {
                    MemoryAccess access1 = _memoryAccesses[i];
                    MemoryAccess access2 = _memoryAccesses[j];

                    // Skip intra-thread accesses (handled by single-threaded compiler)
                    if (access1.VirtualThreadId == access2.VirtualThreadId)
                        continue;

                    // Check for address overlap
                    if (AddressRangesOverlap(access1.Address, access1.Length, access2.Address, access2.Length))
                    {
                        // RAW: earlier write, later read → later VT depends on earlier VT
                        if (access1.IsWrite && !access2.IsWrite)
                        {
                            _dependsOn[access2.VirtualThreadId, access1.VirtualThreadId] = true;
                        }
                        // WAW: both writes → later VT depends on earlier VT (ordering required)
                        else if (access1.IsWrite && access2.IsWrite)
                        {
                            _dependsOn[access2.VirtualThreadId, access1.VirtualThreadId] = true;
                        }
                        // WAR: earlier read, later write → later VT depends on earlier VT (anti-dependency)
                        else if (!access1.IsWrite && access2.IsWrite)
                        {
                            _dependsOn[access2.VirtualThreadId, access1.VirtualThreadId] = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if two address ranges overlap.
        /// Ranges overlap if: addr1 < (addr2 + len2) AND addr2 < (addr1 + len1)
        /// HLS-compatible: no branching, pure arithmetic comparison.
        /// </summary>
        private bool AddressRangesOverlap(ulong addr1, uint len1, ulong addr2, uint len2)
        {
            return (addr1 < (addr2 + len2)) && (addr2 < (addr1 + len1));
        }

        /// <summary>
        /// Returns an array of VT IDs that the given VT depends on.
        /// HLS-compatible: returns fixed-size array with valid count.
        /// </summary>
        public byte[] GetDependencies(byte vtId)
        {
            if (vtId > 3)
                throw new ArgumentOutOfRangeException(nameof(vtId));

            // HLS-compatible: fixed-size result array (max 3 dependencies for 4-way SMT)
            byte[] dependencies = new byte[3];
            int count = 0;

            for (byte j = 0; j < 4; j++)
            {
                if (j != vtId && _dependsOn[vtId, j])
                {
                    dependencies[count] = j;
                    count++;
                }
            }

            // Resize to actual count (HLS note: in hardware, would use valid bits instead)
            if (count < 3)
            {
                byte[] result = new byte[count];
                Array.Copy(dependencies, result, count);
                return result;
            }

            return dependencies;
        }

        /// <summary>
        /// Checks if VT-1 has a direct dependency on VT-2.
        /// Returns true if VT-1 must wait for VT-2 to complete before proceeding.
        /// </summary>
        public bool HasCrossThreadDependency(byte vt1, byte vt2)
        {
            if (vt1 > 3 || vt2 > 3)
                throw new ArgumentOutOfRangeException("VT IDs must be 0-3");

            return _dependsOn[vt1, vt2];
        }

        /// <summary>
        /// Returns the total number of cross-thread dependencies detected.
        /// Useful for compilation statistics.
        /// </summary>
        public int GetDependencyCount()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    if (i != j && _dependsOn[i, j])
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Checks for cyclic dependencies (deadlock detection).
        /// Returns true if a cycle exists (e.g., VT-0 → VT-1 → VT-2 → VT-0).
        /// Uses Floyd's cycle detection algorithm adapted for dependency graphs.
        /// </summary>
        public bool HasCyclicDependency()
        {
            // Simple cycle detection: check if any VT transitively depends on itself
            // Using matrix multiplication: if (dependsOn)^k has diagonal elements, cycle exists
            // Simplified for k=4 (max path length in 4-VT system)

            bool[,] transitive = (bool[,])_dependsOn.Clone();

            // Warshall's algorithm for transitive closure (O(n³) but n=4)
            for (int k = 0; k < 4; k++)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        transitive[i, j] = transitive[i, j] || (transitive[i, k] && transitive[k, j]);
                    }
                }
            }

            // Check diagonal for self-dependencies (cycles)
            for (int i = 0; i < 4; i++)
            {
                if (transitive[i, i])
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Resets the dependency graph (clears all recorded accesses and dependencies).
        /// </summary>
        public void Reset()
        {
            _memoryAccessCount = 0;
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    _dependsOn[i, j] = false;
                }
            }
        }

        /// <summary>
        /// Returns a human-readable string representation of the dependency matrix.
        /// Useful for debugging and visualization.
        /// </summary>
        public string DependencyMatrixToString()
        {
            StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Dependency Matrix (Row depends on Column):");
            sb.Append("   ");
            for (int j = 0; j < 4; j++)
                sb.Append($" VT-{j}");
            sb.AppendLine();

            for (int i = 0; i < 4; i++)
            {
                sb.Append($"VT-{i}");
                for (int j = 0; j < 4; j++)
                {
                    sb.Append(_dependsOn[i, j] ? "  Yes" : "   No");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
