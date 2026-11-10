using System.ComponentModel;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.API.Facade;

/// <summary>
/// Compatibility application-level assembly facade.
/// Prefers canonical compiler emission for supported scalar and memory paths.
/// The scoped control-flow surface uses compiler-native symbolic targets and canonical branch relocation.
/// </summary>
[Obsolete(AsmFacadeDeprecation.Message)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IAppAsmFacade
{
    // ── Arithmetic ──
    void Add(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Sub(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Mul(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void MultiplyWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Div(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void DivideWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void DivideUnsignedWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Mod(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void RemainderWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void RemainderUnsignedWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void SetBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index);
    void ClearBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index);
    void InvertBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index);
    void ExtractBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index);
    void SetBitImmediate(AsmRegister dest, AsmRegister src, int index);
    void ClearBitImmediate(AsmRegister dest, AsmRegister src, int index);
    void InvertBitImmediate(AsmRegister dest, AsmRegister src, int index);
    void ExtractBitImmediate(AsmRegister dest, AsmRegister src, int index);
    void AndWithInvertedSecond(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void OrWithInvertedSecond(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ExclusiveNor(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ScalarMinSigned(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ScalarMaxSigned(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ScalarMinUnsigned(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ScalarMaxUnsigned(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void BinaryPolynomialProductLow(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void BinaryPolynomialProductHigh(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void BinaryPolynomialProductReverse(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ZeroIfConditionEqualZero(AsmRegister dest, AsmRegister src, AsmRegister condition);
    void ZeroIfConditionNotEqualZero(AsmRegister dest, AsmRegister src, AsmRegister condition);
    void AddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftOneAndAdd(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftTwoAndAdd(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftThreeAndAdd(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftOneAndAddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftTwoAndAddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftThreeAndAddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend);
    void ShiftLeftUnsignedWordByImmediate(AsmRegister dest, AsmRegister src, int shift);
    void RotateLeftRegister(AsmRegister dest, AsmRegister src, AsmRegister shift);
    void RotateRightRegister(AsmRegister dest, AsmRegister src, AsmRegister shift);
    void RotateLeftByImmediate(AsmRegister dest, AsmRegister src, int shift);
    void RotateRightByImmediate(AsmRegister dest, AsmRegister src, int shift);
    void CountLeadingZeros(AsmRegister dest, AsmRegister src);
    void CountTrailingZeros(AsmRegister dest, AsmRegister src);
    void CountSetBits(AsmRegister dest, AsmRegister src);
    void ReverseByteOrder(AsmRegister dest, AsmRegister src);
    void ReverseBitsInEachByte(AsmRegister dest, AsmRegister src);
    void SignExtendByte(AsmRegister dest, AsmRegister src);
    void SignExtendHalf(AsmRegister dest, AsmRegister src);
    void SignExtendWord(AsmRegister dest, AsmRegister src);
    void ZeroExtendHalf(AsmRegister dest, AsmRegister src);
    void ZeroExtendWord(AsmRegister dest, AsmRegister src);
    void Sqrt(AsmRegister dest, AsmRegister src);
    void Fmac(AsmRegister acc, AsmRegister src1, AsmRegister src2);
    void Inc(AsmRegister reg);
    void Dec(AsmRegister reg);
    void AddWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void AddWordImmediate(AsmRegister dest, AsmRegister src, int immediate);
    void SubWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ShiftLeft(AsmRegister dest, AsmRegister src, int amount);
    void ShiftLeftWordImmediate(AsmRegister dest, AsmRegister src, int amount);
    void ShiftRight(AsmRegister dest, AsmRegister src, int amount);
    void ShiftRightLogicalWordImmediate(AsmRegister dest, AsmRegister src, int amount);
    void ShiftRightArithmeticWordImmediate(AsmRegister dest, AsmRegister src, int amount);
    void ShiftRightArithmetic(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ShiftLeftWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ShiftRightLogicalWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void ShiftRightArithmeticWord(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Xor(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Or(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void And(AsmRegister dest, AsmRegister src1, AsmRegister src2);
    void Not(AsmRegister dest, AsmRegister src);
    void Nop();
    void LoadImm(AsmRegister dest, ulong value);

    // ── Memory ──
    void Load(ulong address, AsmRegister dest);
    void Store(AsmRegister src, ulong address);
    void Move(AsmRegister src, AsmRegister dest);

    void MtileLoad(
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi);

    void MtileStore(
        CompilerMatrixTileTileOperand sourceTile,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi);

    void MtileMacc(
        CompilerMatrixTileTileOperand leftSourceTile,
        CompilerMatrixTileTileOperand rightSourceTile,
        CompilerMatrixTileTileOperand accumulatorTile,
        CompilerMatrixTileDescriptorAbi leftSourceDescriptor,
        CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicyAbi);

    void Mtranspose(
        CompilerMatrixTileTileOperand sourceTile,
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileDescriptorAbi sourceDescriptor,
        CompilerMatrixTileTransposePolicyAbi transposePolicyAbi);

    // ── Control flow ──
    AsmControlTarget DefineEntryPoint(string name);
    void MarkEntryPoint(AsmControlTarget ep);
    void Jump(AsmControlTarget target);
    void JumpIfNotEqual(AsmControlTarget target, AsmRegister acc, AsmRegister op1, AsmRegister op2, int hint);
    void JumpIfBelow(AsmControlTarget target, AsmRegister acc, AsmRegister op1, AsmRegister op2, int hint);
    void JumpIfAbove(AsmControlTarget target, AsmRegister acc, AsmRegister op1, AsmRegister op2, int hint);
    void Call(AsmControlTarget target, AsmRegister acc, int hint);
    void Return(AsmControlTarget target, AsmRegister acc);

    // ── Parallel hints ──

    /// <summary>
    /// Hint: parallel-for decomposition over [start, end) with the given step.
    /// Compiler may decompose into coordinator + workers. Falls back to sequential if decomposition fails.
    /// </summary>
    void ParallelFor(AsmRegister start, AsmRegister end, AsmRegister step, Action body);

    /// <summary>
    /// Hint: reduction over [start, end) using the specified associative opcode.
    /// Compiler may decompose with worker-local accumulators and coordinator merge. Falls back to sequential if decomposition fails.
    /// </summary>
    void Reduce(AsmRegister accumulator, AsmRegister start, AsmRegister end, Action body);

    // ── Lifecycle ──
    void Init(AsmRegister acc);
}
