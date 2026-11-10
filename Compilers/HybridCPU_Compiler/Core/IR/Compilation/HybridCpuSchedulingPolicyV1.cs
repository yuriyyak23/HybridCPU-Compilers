using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>Immutable identity for existing canonical scheduling selection and bounded search.</summary>
public sealed record HybridCpuSchedulingPolicyV1(
    int MaximumLocalListProgramInstructions,
    int MaximumLocalListBlockInstructions,
    bool UseJointCycleComposition,
    HybridCpuCycleSearchOptionsV1 JointCycleSearchOptions)
{
    public const string SchemaId = "hybridcpu.scheduling-policy/v1";
    public static HybridCpuSchedulingPolicyV1 Default { get; } = new(192, 48, false, HybridCpuCycleSearchOptionsV1.Default);

    public string Digest
    {
        get
        {
            Validate();
            return CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
            {
                SchemaId,
                StructuralMasks = HybridCpuSlotModel.StructuralMaskContract,
                MaximumLocalListProgramInstructions,
                MaximumLocalListBlockInstructions,
                UseJointCycleComposition,
                // Disabled search settings cannot affect output and do not split identity.
                JointSearch = UseJointCycleComposition ? JointCycleSearchOptions.Fingerprint : "disabled"
            }));
        }
    }

    public bool RequiresProgramOrderFallback(IrProgram program)
    {
        Validate();
        return program.Instructions.Count > MaximumLocalListProgramInstructions ||
            program.BasicBlocks.Any(block => block.Instructions.Count > MaximumLocalListBlockInstructions);
    }

    public string ProgramOrderReason(IrProgram program)
    {
        Validate();
        return program.Instructions.Count > MaximumLocalListProgramInstructions
            ? "SchedulerProgramInstructionCap" : "SchedulerBasicBlockInstructionCap";
    }

    private void Validate()
    {
        if (MaximumLocalListProgramInstructions is < 1 or > 192)
            throw new ArgumentOutOfRangeException(nameof(MaximumLocalListProgramInstructions));
        if (MaximumLocalListBlockInstructions is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(MaximumLocalListBlockInstructions));
        if (UseJointCycleComposition)
        {
            ArgumentNullException.ThrowIfNull(JointCycleSearchOptions);
            JointCycleSearchOptions.Validate();
        }
    }
}
