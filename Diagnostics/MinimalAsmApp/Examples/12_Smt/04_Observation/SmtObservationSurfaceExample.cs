using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Smt;

public sealed class SmtObservationSurfaceExample : ICpuExample
{
    public string Name => "smt-observation-surface";

    public string Description => "Documents the observation surfaces exposed for 4-way SMT state inspection.";

    public string Category => "12_Smt/04_Observation";

    public CpuExampleResult Run()
    {
        SmtExampleDescriber.ExpectSmtWayCount(Processor.CPU_Core.SmtWays);

        return CpuExampleResult.Ok(
            "Expected observation surface to expose active VT, per-VT PCs, stalls, stall reasons and registers for vt0..vt3.",
            notes:
            [
                $"SMT ways = {Processor.CPU_Core.SmtWays}",
                $"VT range = vt0..vt{VtId.MaxValue}",
                "IseObservationService.GetActiveVirtualThreadId(coreId)",
                "IseObservationService.GetVirtualThreadLivePcs(coreId)",
                "IseObservationService.GetVirtualThreadStalled(coreId)",
                "IseObservationService.GetVirtualThreadStallReasons(coreId)",
                "IseObservationService.GetVirtualThreadRegisters(coreId, vtId)"
            ]);
    }
}
