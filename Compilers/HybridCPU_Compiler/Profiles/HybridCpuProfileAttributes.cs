namespace HybridCPU.Compiler.Profiles;

/// <summary>Declares a source method or type to be checked against the bounded kernel
/// profile. It is a source-admission assertion, not an execution or memory-isolation grant.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method,
    AllowMultiple = false, Inherited = true)]
public sealed class HybridCpuKernelNoHeapAttribute : Attribute
{
}

/// <summary>Declares a bounded kernel source surface. Use together with
/// <see cref="HybridCpuKernelNoHeapAttribute"/> when allocation is prohibited.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method,
    AllowMultiple = false, Inherited = true)]
public sealed class HybridCpuKernelAttribute : Attribute
{
}

/// <summary>Marks code intended for a fixed-contract software-isolated process.
/// This is declaration metadata only; RuntimeKernel remains authority for domains and capabilities.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method,
    AllowMultiple = false, Inherited = true)]
public sealed class HybridCpuSipAttribute : Attribute
{
}

/// <summary>Marks driver-facing code. It grants neither ECALL authority nor device access.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method,
    AllowMultiple = false, Inherited = true)]
public sealed class HybridCpuDriverAttribute : Attribute
{
}

/// <summary>Declares that a source surface expects no managed collection transition.
/// The CIL importer and final GC/safepoint evidence remain the authority.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method,
    AllowMultiple = false, Inherited = true)]
public sealed class HybridCpuNoGcAttribute : Attribute
{
}

/// <summary>Marks a contract interface for deterministic static source generation. The marker
/// itself has no reflection, dispatch, or runtime registration authority.</summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class HybridCpuStaticContractAttribute : Attribute
{
}
