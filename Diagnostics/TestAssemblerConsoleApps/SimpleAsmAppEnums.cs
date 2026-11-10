namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal enum SimpleAsmAppMode
{
    RefactorShowcase,
    WithoutVirtualThreads,
    WithVirtualThreads,
    SingleThreadNoVector,
    PackedMixedEnvelope,
    Lk,
    Bnmcz
}

internal enum SimpleAsmProgramVariant
{
    RefactorShowcaseComposite,
    NativeVliwPackedScalar,
    NativeVliwPackedMixedEnvelope,
    NativeVliwSingleThread,
    NativeVliwVectorProbe,
    NativeVliwLatencyHidingLoadKernel,
    NativeVliwBankNoConflictMixedZoo
}
