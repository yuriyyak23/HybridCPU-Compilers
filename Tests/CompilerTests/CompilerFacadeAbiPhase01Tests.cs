using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.Threading;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerFacadeAbiPhase01Tests
{
    private static readonly string[] AppFacadeMethodNames =
    [
        nameof(IAppAsmFacade.Add),
        nameof(IAppAsmFacade.AddUnsignedWord),
        nameof(IAppAsmFacade.AddWord),
        nameof(IAppAsmFacade.AddWordImmediate),
        nameof(IAppAsmFacade.And),
        nameof(IAppAsmFacade.AndWithInvertedSecond),
        nameof(IAppAsmFacade.BinaryPolynomialProductHigh),
        nameof(IAppAsmFacade.BinaryPolynomialProductLow),
        nameof(IAppAsmFacade.BinaryPolynomialProductReverse),
        nameof(IAppAsmFacade.Call),
        nameof(IAppAsmFacade.ClearBitImmediate),
        nameof(IAppAsmFacade.ClearBitRegister),
        nameof(IAppAsmFacade.CountLeadingZeros),
        nameof(IAppAsmFacade.Dec),
        nameof(IAppAsmFacade.DefineEntryPoint),
        nameof(IAppAsmFacade.Div),
        nameof(IAppAsmFacade.DivideUnsignedWord),
        nameof(IAppAsmFacade.DivideWord),
        nameof(IAppAsmFacade.Fmac),
        nameof(IAppAsmFacade.CountTrailingZeros),
        nameof(IAppAsmFacade.CountSetBits),
        nameof(IAppAsmFacade.Inc),
        nameof(IAppAsmFacade.Init),
        nameof(IAppAsmFacade.ExtractBitImmediate),
        nameof(IAppAsmFacade.ExtractBitRegister),
        nameof(IAppAsmFacade.InvertBitImmediate),
        nameof(IAppAsmFacade.InvertBitRegister),
        nameof(IAppAsmFacade.Jump),
        nameof(IAppAsmFacade.JumpIfAbove),
        nameof(IAppAsmFacade.JumpIfBelow),
        nameof(IAppAsmFacade.JumpIfNotEqual),
        nameof(IAppAsmFacade.Load),
        nameof(IAppAsmFacade.LoadImm),
        nameof(IAppAsmFacade.MarkEntryPoint),
        nameof(IAppAsmFacade.Mod),
        nameof(IAppAsmFacade.Move),
        nameof(IAppAsmFacade.MtileLoad),
        nameof(IAppAsmFacade.MtileMacc),
        nameof(IAppAsmFacade.MtileStore),
        nameof(IAppAsmFacade.Mtranspose),
        nameof(IAppAsmFacade.Mul),
        nameof(IAppAsmFacade.MultiplyWord),
        nameof(IAppAsmFacade.Nop),
        nameof(IAppAsmFacade.Not),
        nameof(IAppAsmFacade.Or),
        nameof(IAppAsmFacade.OrWithInvertedSecond),
        nameof(IAppAsmFacade.ParallelFor),
        nameof(IAppAsmFacade.Reduce),
        nameof(IAppAsmFacade.RemainderUnsignedWord),
        nameof(IAppAsmFacade.RemainderWord),
        nameof(IAppAsmFacade.Return),
        nameof(IAppAsmFacade.ExclusiveNor),
        nameof(IAppAsmFacade.ReverseByteOrder),
        nameof(IAppAsmFacade.ReverseBitsInEachByte),
        nameof(IAppAsmFacade.RotateLeftByImmediate),
        nameof(IAppAsmFacade.RotateLeftRegister),
        nameof(IAppAsmFacade.RotateRightByImmediate),
        nameof(IAppAsmFacade.RotateRightRegister),
        nameof(IAppAsmFacade.ScalarMaxSigned),
        nameof(IAppAsmFacade.ScalarMaxUnsigned),
        nameof(IAppAsmFacade.ScalarMinSigned),
        nameof(IAppAsmFacade.ScalarMinUnsigned),
        nameof(IAppAsmFacade.ShiftLeft),
        nameof(IAppAsmFacade.ShiftLeftOneAndAdd),
        nameof(IAppAsmFacade.ShiftLeftOneAndAddUnsignedWord),
        nameof(IAppAsmFacade.ShiftLeftThreeAndAdd),
        nameof(IAppAsmFacade.ShiftLeftThreeAndAddUnsignedWord),
        nameof(IAppAsmFacade.ShiftLeftTwoAndAdd),
        nameof(IAppAsmFacade.ShiftLeftTwoAndAddUnsignedWord),
        nameof(IAppAsmFacade.ShiftLeftUnsignedWordByImmediate),
        nameof(IAppAsmFacade.ShiftLeftWord),
        nameof(IAppAsmFacade.ShiftLeftWordImmediate),
        nameof(IAppAsmFacade.ShiftRight),
        nameof(IAppAsmFacade.ShiftRightArithmetic),
        nameof(IAppAsmFacade.ShiftRightArithmeticWord),
        nameof(IAppAsmFacade.ShiftRightArithmeticWordImmediate),
        nameof(IAppAsmFacade.ShiftRightLogicalWord),
        nameof(IAppAsmFacade.ShiftRightLogicalWordImmediate),
        nameof(IAppAsmFacade.SetBitImmediate),
        nameof(IAppAsmFacade.SetBitRegister),
        nameof(IAppAsmFacade.SignExtendByte),
        nameof(IAppAsmFacade.SignExtendHalf),
        nameof(IAppAsmFacade.SignExtendWord),
        nameof(IAppAsmFacade.Sqrt),
        nameof(IAppAsmFacade.Store),
        nameof(IAppAsmFacade.Sub),
        nameof(IAppAsmFacade.SubWord),
        nameof(IAppAsmFacade.Xor),
        nameof(IAppAsmFacade.ZeroIfConditionEqualZero),
        nameof(IAppAsmFacade.ZeroIfConditionNotEqualZero),
        nameof(IAppAsmFacade.ZeroExtendHalf),
        nameof(IAppAsmFacade.ZeroExtendWord),
    ];

    private static readonly string[] PlatformFacadeDeclaredMethodNames =
    [
        nameof(IPlatformAsmFacade.ReadSystemCycleCounter),
        nameof(IPlatformAsmFacade.CsrClear),
        nameof(IPlatformAsmFacade.CsrRead),
        nameof(IPlatformAsmFacade.CsrWrite),
        nameof(IPlatformAsmFacade.VLoad),
        nameof(IPlatformAsmFacade.VStore),
        nameof(IPlatformAsmFacade.VectorOp),
        nameof(IPlatformAsmFacade.VectorOpImm),
        nameof(IPlatformAsmFacade.VSetVli),
    ];

    private static readonly string[] ClosedHelperExactNames =
    [
        "Atomic",
        "Amo",
        "CacheClean",
        "CacheFlush",
        "CacheInvalidate",
        "CarryLessMultiply",
        "ClMul",
        "ClMulH",
        "ClMulR",
        "Crc32",
        "Crc64",
        "Adc",
        "Sbc",
        "Addc",
        "Subc",
        "AddWithCarry",
        "AddCarry",
        "SubtractWithBorrow",
        "SubWithBorrow",
        "SubBorrow",
        "CompileAcceleratorBackend",
        "CompileAcceleratorCancel",
        "CompileAcceleratorFence",
        "CompileAcceleratorFft",
        "CompileAcceleratorFallback",
        "CompileAcceleratorGetResult",
        "CompileAcceleratorPoll",
        "CompileAcceleratorQueryCaps",
        "CompileAcceleratorReset",
        "CompileAcceleratorSparseGraph",
        "CompileAcceleratorStatus",
        "CompileAcceleratorTensor",
        "CompileAcceleratorTopology",
        "CompileAcceleratorWait",
        "CompileDmaStreamBackend",
        "CompileDmaStreamFallback",
        "CompileDmaStreamQueryCaps",
        "CompileDmaStreamQueue",
        "CompileDmaStreamStatus",
        "CompileDscQueryCaps",
        "CompileDscStatus",
        "CompileVmcs",
        "CompileVmx",
        "CountPopulation",
        "Cpop",
        "CPop",
        "CzeroEqz",
        "PopulationCount",
        "CZeroEqz",
        "Fence",
        "FenceI",
        "LoadReserved",
        "MaskedFence",
        "Matrix",
        "Max",
        "Min",
        "RdCycle",
        "ReadCycle",
        "RotateLeft",
        "RotateLeftImmediate",
        "RotateRight",
        "RotateRightImmediate",
        "Roli",
        "Rori",
        "Bset",
        "Bclr",
        "Binv",
        "Bext",
        "Bseti",
        "Bclri",
        "Binvi",
        "Bexti",
        "BitSet",
        "BitClear",
        "BitInvert",
        "BitExtract",
        "SextB",
        "SextH",
        "Sh1Add",
        "Sh2Add",
        "Sh3Add",
        "AddUw",
        "Sh1AddUw",
        "Sh2AddUw",
        "Sh3AddUw",
        "SlliUw",
        "SfenceVma",
        "StoreConditional",
        "Tile",
        "TlbFlush",
        "VaddSat",
        "Vcvt",
        "VdotWide",
        "VGather",
        "VectorAddSaturating",
        "VectorDotWide",
        "VectorGather",
        "VectorLoad",
        "VectorMaskPrefix",
        "VectorPermute2",
        "VectorScanSum",
        "VectorScatter",
        "VectorSlide1Down",
        "VectorSlide1Up",
        "VectorStore",
        "VectorTranspose",
        "VectorZeroExtend",
        "VdotAccum",
        "VdotBlockscale",
        "VdotWideI16",
        "VdotWideI32",
        "Blockscale",
        "BlockScaledDot",
        "DotAccum",
        "DscSub",
        "DscMin",
        "DscMax",
        "DscAbsDiff",
        "DscClamp",
        "DscConvert",
        "DscCompare",
        "DscSelect",
        "DscReduceSum",
        "DscReduceMin",
        "DscReduceMax",
        "DscReduceAnd",
        "DscReduceOr",
        "DscReduceXor",
        "DscShape",
        "DmaStreamComputeSub",
        "DmaStreamComputeConvert",
        "DmaStreamComputeReduceSum",
        "DscPoll",
        "DscWait",
        "DscCancel",
        "DscFence",
        "DscCommit",
        "DscQueryBackend",
        "DscQueryShape",
        "Dsc2",
        "DmaStreamComputePoll",
        "DmaStreamComputeWait",
        "DmaStreamComputeCancel",
        "DmaStreamComputeFence",
        "DmaStreamComputeCommit",
        "DmaStreamComputeQueryBackend",
        "DmaStreamComputeQueryShape",
        "DmaStreamComputeQueue",
        "DmaStreamComputeDsc2",
        "Vmsbf",
        "Vperm2",
        "VscanSum",
        "VScatter",
        "Vslide1Down",
        "Vslide1Up",
        "Vtranspose",
        "Vzext",
        "ZextH",
    ];

    [Fact]
    public void CompatibilityFacadeTypes_RemainObsoleteAndHidden()
    {
        AssertCompatibilityOnlyFacade(typeof(IAppAsmFacade));
        AssertCompatibilityOnlyFacade(typeof(AppAsmFacade));
        AssertCompatibilityOnlyFacade(typeof(IPlatformAsmFacade));
        AssertCompatibilityOnlyFacade(typeof(PlatformAsmFacade));
    }

    [Fact]
    public void AppFacadePublicAbi_StaysCompatibilityInventoryWithMatrixTilePositiveHandoff()
    {
        AssertPublicDeclaredMethodNames(typeof(IAppAsmFacade), AppFacadeMethodNames);
        AssertPublicDeclaredMethodNames(typeof(AppAsmFacade), AppFacadeMethodNames);
    }

    [Fact]
    public void PlatformFacadePublicAbi_IncludesTypedVectorTransferAndScopedRawTransport()
    {
        AssertPublicDeclaredMethodNames(typeof(IPlatformAsmFacade), PlatformFacadeDeclaredMethodNames);
        AssertPublicDeclaredMethodNames(typeof(PlatformAsmFacade), PlatformFacadeDeclaredMethodNames);
    }

    [Fact]
    public void FacadeAndThreadCompilerPublicAbi_DoNotPublishAdjacentClosedHelpers()
    {
        string[] publicMethods =
        [
            .. PublicDeclaredMethodNames(typeof(IAppAsmFacade)),
            .. PublicDeclaredMethodNames(typeof(AppAsmFacade)),
            .. PublicDeclaredMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicDeclaredMethodNames(typeof(PlatformAsmFacade)),
            .. PublicDeclaredMethodNames(typeof(HybridCpuThreadCompilerContext)),
        ];

        foreach (string closedHelperName in ClosedHelperExactNames)
        {
            Assert.DoesNotContain(
                publicMethods,
                methodName => string.Equals(methodName, closedHelperName, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void VectorFacadeEscapeHatches_IncludeTypedTransferAndScopedRawTransport()
    {
        string[] platformMethods = PublicDeclaredMethodNames(typeof(IPlatformAsmFacade));
        string[] vectorNamedMethods = platformMethods
            .Where(static name => name.Contains("Vector", StringComparison.Ordinal) ||
                                  name.StartsWith('V'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                nameof(IPlatformAsmFacade.VLoad),
                nameof(IPlatformAsmFacade.VSetVli),
                nameof(IPlatformAsmFacade.VStore),
                nameof(IPlatformAsmFacade.VectorOp),
                nameof(IPlatformAsmFacade.VectorOpImm),
            ],
            vectorNamedMethods);
    }

    private static void AssertCompatibilityOnlyFacade(Type type)
    {
        Assert.NotNull(type.GetCustomAttribute<ObsoleteAttribute>());

        EditorBrowsableAttribute? editorBrowsable =
            type.GetCustomAttribute<EditorBrowsableAttribute>();
        Assert.NotNull(editorBrowsable);
        Assert.Equal(EditorBrowsableState.Never, editorBrowsable.State);
    }

    private static void AssertPublicDeclaredMethodNames(Type type, string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            PublicDeclaredMethodNames(type).Order(StringComparer.Ordinal));
    }

    private static string[] PublicDeclaredMethodNames(Type type)
    {
        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .ToArray();
    }
}
