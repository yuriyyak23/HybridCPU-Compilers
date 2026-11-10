using System.ComponentModel;
using HybridCPU.Compiler.Core.IR;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;

namespace HybridCPU.Compiler.Core.API.Facade;

/// <summary>
/// Compatibility platform-level assembly facade for OS bring-up and system programming.
/// Covers Vector (RVV) and canonical CSR/system control helpers.
/// Does NOT expose typed-slot internals.
/// </summary>
[Obsolete(AsmFacadeDeprecation.Message)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPlatformAsmFacade : IAppAsmFacade
{
    void ReadSystemCycleCounter(AsmRegister dest);

    void CsrRead(AsmRegister dest, ushort csrAddr);
    void CsrWrite(ushort csrAddr, AsmRegister src);
    void CsrClear();

    void VectorOp(
        Processor.CPU_Core.IsaOpcode op,
        DataTypeEnum dataType,
        ulong dest,
        ulong src,
        uint streamLength,
        ushort stride);

    void VectorOpImm(
        Processor.CPU_Core.IsaOpcode op,
        DataTypeEnum dataType,
        ushort immediate,
        ulong dest,
        ulong src,
        uint streamLength,
        ushort stride);

    void VLoad(
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferShapeAbi shape);

    void VStore(
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferShapeAbi shape);

    void VSetVli(
        AsmRegister vlReg,
        AsmRegister avlReg,
        DataTypeEnum dataType);
}
