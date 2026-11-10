### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
HC0004 | HybridCPU.Profile | Error | Rejects source heap-allocation syntax in Kernel.NoHeap.
HC0005 | HybridCPU.Profile | Error | Rejects known runtime allocation, reflection, and thread APIs in Kernel.NoHeap.
HC0006 | HybridCPU.Profile | Error | Rejects delegates/closures and async/await in Kernel.NoHeap.
