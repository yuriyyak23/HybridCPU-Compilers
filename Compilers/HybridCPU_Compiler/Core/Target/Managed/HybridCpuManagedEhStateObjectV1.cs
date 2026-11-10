using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Single-context restricted-profile EH state. The first word is an explicit
/// static GC root while dispatch, finally unwind, or a catch is active. The image-owned
/// transfer record keeps exceptional resume state alive across a non-returning finally transfer.</summary>
public static class HybridCpuManagedEhStateObjectV1
{
    public const string Symbol = "__hybridcpu_managed_eh_state";
    public const string RootSymbol = "__hybridcpu_managed_eh_current_exception";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-state/v2";
    public const int ExceptionReferenceOffset = 0;
    public const int TypeHandleOffset = 8;
    public const int DispatchActiveOffset = 16;
    public const int DispatchPhaseOffset = 24;
    public const int TargetMethodRowOffset = 32;
    public const int TargetClauseOffset = 40;
    public const int WalkerProgramCounterOffset = 48;
    public const int WalkerFirstFrameOffset = 56;
    public const int WalkerLastFinallyTrySizeOffset = 64;
    public const int SearchTransferRecordOffset = 80;
    public const int UnwindTransferRecordOffset = SearchTransferRecordOffset +
        HybridCPU.Platform.Contracts.HybridCpuManagedExceptionTransferV1.SizeBytes;
    public const int SizeBytes = UnwindTransferRecordOffset +
        HybridCPU.Platform.Contracts.HybridCpuManagedExceptionTransferV1.SizeBytes;

    public static HybridCpuObjectArtifactV1 Emit() => new HybridCpuObjectWriterV1().Write(new(
        [new(".hcehstate", HybridCpuObjectSectionKind.ZeroFill, 16, [], SizeBytes)],
        [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcehstate", 0, SizeBytes, true),
         new(RootSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcehstate",
             ExceptionReferenceOffset, 8, true)], [], HybridCpuTargetPlatformContractV1.Default.ContractDigest,
        HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
}
