using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;

RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
    .ImportFile(args[0], new("HybridCPU.ExternalRuntime.Tests.CpuBackedChildExecutionTests", "LongRun"));
Console.WriteLine("Import=" + imported.Status);
if (imported.Program is not { } program) { Console.WriteLine(string.Join(";", imported.Diagnostics)); return 1; }
IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
    resourceModel: HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8), options: HybridCpuRegisterAllocationOptionsV1.Qualification);
Console.WriteLine("Allocation=" + allocation.Status + ":" + allocation.Reason);
foreach (IrInstruction? instruction in program.Instructions.Take(3))
    Console.WriteLine($"Imported {instruction.Opcode}: operands={string.Join(';',instruction.Operands)} uses={string.Join(';',instruction.Annotation.Uses)} defs={string.Join(';',instruction.Annotation.Defs)}");
if (allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.FinalBundles is null) return 2;
IReadOnlyList<HybridCpuInstructionBundle> words = new HybridCpuBundleLowerer().LowerProgram(allocation.FinalBundles);
byte[] encoded = new HybridCpuBundleSerializer().SerializeProgram(words);
Console.WriteLine("FirstWord=" + Convert.ToHexString(encoded.AsSpan(0, 32)));
return 0;


