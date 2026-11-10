using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Smt;

public sealed class VirtualThreadIdRangeExample : ICpuExample
{
    public string Name => "smt-vt-id-range";

    public string Description => "Shows that the CPU exposes four valid virtual thread ids: vt0..vt3.";

    public string Category => "12_Smt/00_4WayVliwSmt";

    public CpuExampleResult Run()
    {
        SmtExampleDescriber.ExpectSmtWayCount(Processor.CPU_Core.SmtWays);

        VtId[] validIds =
        [
            VtId.Create(0),
            VtId.Create(1),
            VtId.Create(2),
            VtId.Create(3)
        ];

        bool invalidRejected = !VtId.TryCreate(4, out _);
        if (!invalidRejected)
        {
            throw new InvalidOperationException("vt4 must be rejected for a 4-way SMT core.");
        }

        return CpuExampleResult.Ok(
            "Expected valid VT ids vt0..vt3 and rejection of vt4.",
            notes:
            [
                $"Processor.CPU_Core.SmtWays = {Processor.CPU_Core.SmtWays}",
                $"VtId.SmtWayCount = {VtId.SmtWayCount}",
                $"valid = {string.Join(", ", validIds.Select(id => id.ToString()))}",
                "vt4 accepted = false"
            ]);
    }
}
