namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static bool IsCheckedScalarArithmetic(ResolvedHelper helper) =>
        !helper.IsManagedCall &&
        helper.Contract.StableIdentity == helper.Identity &&
        helper.Contract.CanonicalExpansion == "runtime-helper" &&
        (helper.Identity is "checked-div-i8" or "checked-rem-i8" &&
            helper.Contract.ReturnType == RestrictedCilTypeV1.Int64 &&
            helper.Contract.ParameterTypes.SequenceEqual(new[] { RestrictedCilTypeV1.Int64, RestrictedCilTypeV1.Int64 }) ||
         helper.Identity is "checked-divu-i8" or "checked-remu-i8" && helper.Contract.ParameterTypes.Count == 2 &&
            UnsignedDivisionWidth(helper.Contract.ParameterTypes[0], helper.Contract.ParameterTypes[1]) == 64 &&
            helper.Contract.ReturnType == helper.Contract.ParameterTypes[0] ||
         helper.Identity is "checked-div-i4" or "checked-rem-i4" &&
            helper.Contract.ReturnType == RestrictedCilTypeV1.Int32 &&
            helper.Contract.ParameterTypes.SequenceEqual(new[] { RestrictedCilTypeV1.Int32, RestrictedCilTypeV1.Int32 }) ||
         helper.Identity is "checked-divu-i4" or "checked-remu-i4" && helper.Contract.ParameterTypes.Count == 2 &&
            UnsignedDivisionWidth(helper.Contract.ParameterTypes[0], helper.Contract.ParameterTypes[1]) == 32 &&
            helper.Contract.ReturnType == helper.Contract.ParameterTypes[0]);

    private static int UnsignedDivisionWidth(RestrictedCilTypeV1 left, RestrictedCilTypeV1 right) =>
        left is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 && right is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 ? 32 :
        left is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 && right is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 ? 64 : 0;

    private static bool ProvenNonzeroDivisor(V2Value value, int width) =>
        value.Operand.Kind == HybridCPU.Compiler.Core.IR.IrOperandKind.Constant &&
        (width == 32 ? unchecked((uint)value.Operand.Value) != 0 : value.Operand.Value != 0);

    private static bool ProvenSafeSignedDivision(V2Value numerator, V2Value divisor, int width)
    {
        if (divisor.Operand.Kind != HybridCPU.Compiler.Core.IR.IrOperandKind.Constant) return false;
        long signedDivisor = width == 32
            ? unchecked((int)(uint)divisor.Operand.Value)
            : unchecked((long)divisor.Operand.Value);
        if (signedDivisor == 0) return false;
        if (signedDivisor != -1) return true;
        if (numerator.Operand.Kind != HybridCPU.Compiler.Core.IR.IrOperandKind.Constant) return false;
        return width == 32
            ? unchecked((int)(uint)numerator.Operand.Value) != int.MinValue
            : unchecked((long)numerator.Operand.Value) != long.MinValue;
    }
}
