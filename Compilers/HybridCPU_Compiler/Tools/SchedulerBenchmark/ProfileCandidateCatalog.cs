using System.Numerics;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Native;

internal static class ProfileCandidateCatalog
{
    public static void Run(string output)
    {
        List<object> rows = new List<object>();
        int rejected = 0;
        foreach (HybridCpuOpcode opcode in Enum.GetValues<HybridCpuOpcode>().Distinct().OrderBy(o => (uint)o))
        {
            // Single documented register-carrier template: rejection is not opcode-wide unsupportedness.
            NativeCompilationResult result = new NativeAssemblyFrontend().Compile(new NativeFrontendRequest(0, $"{opcode} r10, r30, r31"));
            if (!result.Succeeded) { rejected++; continue; }
            IrInstruction instruction = result.Artifacts!.CanonicalIr.Instructions.Single();
            IrInstructionAnnotation annotation = instruction.Annotation;
            rows.Add(new { Opcode = opcode.ToString(), SlotClass = annotation.RequiredSlotClass.ToString(),
                annotation.MinimumLatencyCycles, SlotFlexibility = BitOperations.PopCount((uint)annotation.StructurallyAllowedSlots),
                Slots = annotation.StructurallyAllowedSlots.ToString(), annotation.Serialization });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Schema = "hybridcpu.profile-candidate-catalog/v1",
            Qualification = "no", Template = "opcode r10,r30,r31", RejectedTemplates = rejected, Rows = rows },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS profile candidate catalog: {rows.Count} admitted, {rejected} rejected templates; Qualification: no");
    }
}
