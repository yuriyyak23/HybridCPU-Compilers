using CpuInterfaceBridge.Diagnostics;
using HybridCPU_ISE;
using HybridCPU_ISE.Machine;

// Uses the actual ISE observation types, but no Processor globals, loader or CPU execution.
IseObservationService unavailable = new IseObservationService(NullMachineStateSource.Instance, new object());
try
{
    using HostObservationEndpoint endpoint = IseHostObservationAdapter.Create("unavailable fixture", unavailable);
    throw new Exception("Null observation source was incorrectly admitted.");
}
catch (ArgumentException ex) when (ex.ParamName == "sourceKind")
{
    Console.WriteLine("PASS: real ISE Null source rejected before any snapshot/CPU read.");
}
Console.WriteLine("Real adapter smoke passed; no guest execution or external process attach.");
