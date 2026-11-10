using System;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Machine;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

/// <summary>
/// Centralizes the legacy Processor bootstrap and runtime access surface used by
/// TestAssemblerConsoleApps so architectural probes do not reach into the global
/// compat runtime from every workload file.
/// </summary>
internal sealed class DiagnosticRuntimeSession
{
    public const int DefaultCoreId = 0;

    public DiagnosticRuntimeSession(int coreId = DefaultCoreId)
    {
        if (coreId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coreId), coreId, "Core id must be non-negative.");
        }

        CoreId = coreId;
    }

    public int CoreId { get; }

    public void BootstrapCompilerRuntime()
    {
#pragma warning disable CS0618
        _ = new Processor(ProcessorMode.Compiler);
#pragma warning restore CS0618

        DiagnosticShadowFrontend.ConfigureBootstrappedCores();

        Processor.ConfigureProfiling(true, ProfilingOptions.Default());
        Processor.ResetPerformanceCounters();
    }

    public void ResetProfiling()
    {
        Processor.ConfigureProfiling(false);
    }

    public void WriteMemory(ulong address, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Processor.MainMemory.WriteToPosition(bytes, address);
    }

    public void PublishBundleAnnotations(ulong bundleAddress, VliwBundleAnnotations annotations)
    {
        Processor.MainMemory.PublishVliwBundleAnnotations(bundleAddress, annotations);
    }

    public void WriteCommittedRegister(byte virtualThreadId, int registerId, ulong value)
    {
        Processor.CPU_Core core = GetCoreRef();
        core.WriteCommittedArch(virtualThreadId, registerId, value);
    }

    public void PrepareExecutionStart(ulong pc)
    {
        Processor.CurrentProcessorMode = ProcessorMode.Emulation;
        Processor.CPU_Core core = GetCoreRef();
        core.PrepareExecutionStart(pc);
    }

    public void ConfigureFspForDiagnostics()
    {
        Processor.CPU_Core core = GetCoreRef();
        core.VectorConfig.FSP_Enabled = 1;
        core.VectorConfig.FSP_StealMask = 0xFF;

        PodController? pod = Processor.GetPodForCore(CoreId);
        if (pod?.Scheduler is { } scheduler)
        {
            scheduler.TypedSlotEnabled = true;
        }
    }

    public void SetPipelineMode(bool enabled)
    {
        Processor.CPU_Core core = GetCoreRef();
        core.SetPipelineMode(enabled);
    }

    public void ExecutePipelineCycle()
    {
        Processor.CPU_Core core = GetCoreRef();
        core.ExecutePipelineCycle();
    }

    public ulong ReadActiveLivePc()
    {
        return GetCoreSnapshot().ReadActiveLivePc();
    }

    public int ReadActiveVirtualThreadId()
    {
        return GetCoreSnapshot().ReadActiveVirtualThreadId();
    }

    public Processor.CPU_Core.PipelineControl GetPipelineControl()
    {
        return GetCoreSnapshot().GetPipelineControl();
    }

    public Processor.CPU_Core.PipelineControl CapturePipelineControl()
    {
        return Processor.CPU_Cores != null &&
               CoreId < Processor.CPU_Cores.Length
            ? GetCoreSnapshot().GetPipelineControl()
            : default;
    }

    public PerformanceReport GetPerformanceStats()
    {
        return Processor.GetPerformanceStats();
    }

    public PerformanceReport CapturePerformanceStats()
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

    public Processor.CPU_Core GetCoreRef() => Processor.GetCoreRef(CoreId);

    public CpuCoreDiagnosticSnapshot GetCoreSnapshot() => Processor.GetCoreSnapshot(CoreId);

    public Processor.CPU_Core.LiveCpuStateAdapter CreateLiveCpuStateAdapter(byte virtualThreadId)
    {
        Processor.CPU_Core core = GetCoreRef();
        return core.CreateLiveCpuStateAdapter(virtualThreadId);
    }

    public void ApplyLiveStateAdapter(Processor.CPU_Core.LiveCpuStateAdapter state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Processor.CPU_Core core = GetCoreRef();
        state.ApplyTo(ref core);
    }
}
