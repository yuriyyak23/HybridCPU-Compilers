using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Contracts;
using YAKSys_Hybrid_CPU.Core.Vmcs.V2;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerNoEmissionBoundaryTests
{
    private static readonly string[] ClosedHelperNameFragments =
    [
        "Atomic",
        "Amo",
        "LoadReserved",
        "StoreConditional",
        "Fence",
        "Sfence",
        "DCache",
        "ICache",
        "Tlb",
        "Matrix",
        "Mtile",
        "Cache",
        "Iotlb",
        "Iommu",
        "Seqz",
        "Snez",
        "Csel",
        "Czero",
        "Cpop",
        "Clz",
        "Ctz",
        "Popcnt",
        "PopulationCount",
        "Rol",
        "Ror",
        "Roli",
        "Rori",
        "Rev8",
        "Brev8",
        "RotateLeftImmediate",
        "RotateRightImmediate",
        "ReverseBytes",
        "ReverseBitsInByte",
        "Andn",
        "Orn",
        "Xnor",
        "SextB",
        "SextH",
        "ZextH",
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
        "Bitfield",
        "AddUw",
        "Sh1AddUw",
        "Sh2AddUw",
        "Sh3AddUw",
        "SlliUw",
        "RdCycle",
        "RdTime",
        "RdInstret",
        "Pause",
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
        "VMerge",
        "VSelect",
        "VFirst",
        "VAny",
        "VAll",
        "Vmsbf",
        "Vmsif",
        "Vmsof",
        "Vzext",
        "Vscan",
        "Vldseg",
        "Vstseg",
        "Vzip",
        "Vunzip",
        "Vinterleave",
        "Vdeinterleave",
        "Saturating",
        "Saturate",
        "VaddSat",
        "SatAdd",
        "FixedPoint",
        "Vclip",
        "Vavg",
        "Widen",
        "Narrow",
        "Vwadd",
        "Vn",
        "Vcvt",
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
        "AcceleratorStatus",
        "AcceleratorGetResult",
        "AcceleratorReset",
        "AcceleratorQueryAbi",
        "AcceleratorQueryTopology",
        "AcceleratorTopology",
        "AcceleratorTensor",
        "AcceleratorOpen",
        "AcceleratorClose",
        "AcceleratorBindQueue",
        "AcceleratorUnbindQueue",
        "AcceleratorFft",
        "AcceleratorDsp",
        "AcceleratorCrypto",
        "AcceleratorCryptoHash",
        "AcceleratorHash",
        "AcceleratorCompression",
        "AcceleratorSparse",
        "AcceleratorSparseGraph",
        "AcceleratorGraph",
        "AcceleratorMedia",
        "Vmx",
        "Vmcs",
        "VmFunc",
        "VmRead",
        "VmWrite",
        "VmPtr",
        "VmLaunch",
        "VmResume",
        "DirtyLog",
        "Checkpoint",
        "Migration",
        "Observability",
        "DebugTrace",
        "TraceHandle",
        "Vmcs12",
        "Vmcs02"
    ];

    private static readonly (string ContourName, string[] PublicHelperFragments, string[] CompilerSourceFragments)[] RuntimeOnlyVectorContours =
    [
        ("VGATHER",
            ["VGather", "VectorGather", "GatherIndexed", "IndexedGather"],
            ["InstructionsEnum.VGATHER", "IsaOpcodeValues.VGATHER", "OpcodeValues.VGATHER", "CompileVGather", "EmitVGather", "CompileVectorGather", "EmitVectorGather", "CompileGatherIndexed", "EmitGatherIndexed"]),
        ("VSCATTER",
            ["VScatter", "VectorScatter", "ScatterIndexed", "IndexedScatter"],
            ["InstructionsEnum.VSCATTER", "IsaOpcodeValues.VSCATTER", "OpcodeValues.VSCATTER", "CompileVScatter", "EmitVScatter", "CompileVectorScatter", "EmitVectorScatter", "CompileScatterIndexed", "EmitScatterIndexed"]),
        ("VMSBF",
            ["Vmsbf", "VectorMaskPrefix", "MaskPrefix", "MaskSetBeforeFirst"],
            ["InstructionsEnum.VMSBF", "IsaOpcodeValues.VMSBF", "OpcodeValues.VMSBF", "CompileVmsbf", "EmitVmsbf", "CompileVectorMaskPrefix", "EmitVectorMaskPrefix", "CompileMaskSetBeforeFirst", "EmitMaskSetBeforeFirst"]),
        ("VZEXT",
            ["Vzext", "VectorZeroExtend", "VectorWiden", "VectorNarrow", "VectorConvert"],
            ["InstructionsEnum.VZEXT", "IsaOpcodeValues.VZEXT", "OpcodeValues.VZEXT", "CompileVzext", "EmitVzext", "CompileVectorZeroExtend", "EmitVectorZeroExtend", "CompileVectorWiden", "CompileVectorNarrow", "CompileVectorConvert"]),
        ("VSCAN.SUM",
            ["Vscan", "VscanSum", "VectorScanSum", "ScanSum"],
            ["InstructionsEnum.VSCAN_SUM", "IsaOpcodeValues.VSCAN_SUM", "OpcodeValues.VSCAN_SUM", "CompileVscanSum", "EmitVscanSum", "CompileVectorScanSum", "EmitVectorScanSum"]),
        ("VADD.SAT",
            ["VaddSat", "VectorAddSaturating", "SaturatingAdd", "SaturatingVectorAdd"],
            ["InstructionsEnum.VADD_SAT", "IsaOpcodeValues.VADD_SAT", "OpcodeValues.VADD_SAT", "CompileVaddSat", "EmitVaddSat", "CompileVectorAddSaturating", "EmitVectorAddSaturating", "SaturatingVectorAdd"]),
        ("VSLIDE1UP",
            ["Vslide1Up", "Slide1Up", "SlideOneUp", "VectorSlide1Up"],
            ["InstructionsEnum.VSLIDE1UP", "IsaOpcodeValues.VSLIDE1UP", "OpcodeValues.VSLIDE1UP", "CompileVslide1Up", "EmitVslide1Up", "CompileSlideOneUp", "EmitSlideOneUp", "CompileVectorSlide1Up", "EmitVectorSlide1Up"]),
        ("VDOT.WIDE",
            ["VdotWide", "DotWide", "VectorDotWide", "MixedPrecisionDot", "BlockScaledDot"],
            ["InstructionsEnum.VDOT_WIDE", "IsaOpcodeValues.VDOT_WIDE", "OpcodeValues.VDOT_WIDE", "CompileVdotWide", "EmitVdotWide", "CompileDotWide", "EmitDotWide", "CompileMixedPrecisionDot", "CompileBlockScaledDot"]),
        ("VDOT.BLOCKSCALE",
            ["VdotBlockscale", "VdotBlockScale", "BlockscaleDot", "BlockScaledDot"],
            ["InstructionsEnum.VDOT_BLOCKSCALE", "IsaOpcodeValues.VDOT_BLOCKSCALE", "OpcodeValues.VDOT_BLOCKSCALE", "CompileVdotBlockscale", "EmitVdotBlockscale", "CompileBlockscaleDot", "CompileBlockScaledDot"]),
        ("VDOT.ACCUM",
            ["VdotAccum", "DotAccum", "VectorDotAccum"],
            ["InstructionsEnum.VDOT_ACCUM", "IsaOpcodeValues.VDOT_ACCUM", "OpcodeValues.VDOT_ACCUM", "CompileVdotAccum", "EmitVdotAccum", "CompileDotAccum", "CompileVectorDotAccum"]),
        ("VDOT.WIDE.I16",
            ["VdotWideI16", "DotWideI16", "VectorDotWideI16"],
            ["InstructionsEnum.VDOT_WIDE_I16", "IsaOpcodeValues.VDOT_WIDE_I16", "OpcodeValues.VDOT_WIDE_I16", "CompileVdotWideI16", "EmitVdotWideI16", "CompileDotWideI16", "CompileVectorDotWideI16"]),
        ("VDOT.WIDE.I32",
            ["VdotWideI32", "DotWideI32", "VectorDotWideI32"],
            ["InstructionsEnum.VDOT_WIDE_I32", "IsaOpcodeValues.VDOT_WIDE_I32", "OpcodeValues.VDOT_WIDE_I32", "CompileVdotWideI32", "EmitVdotWideI32", "CompileDotWideI32", "CompileVectorDotWideI32"]),
        ("VSLIDE1DOWN",
            ["Vslide1Down", "Slide1Down", "SlideOneDown", "VectorSlide1Down"],
            ["InstructionsEnum.VSLIDE1DOWN", "IsaOpcodeValues.VSLIDE1DOWN", "OpcodeValues.VSLIDE1DOWN", "CompileVslide1Down", "EmitVslide1Down", "CompileSlideOneDown", "EmitSlideOneDown", "CompileVectorSlide1Down", "EmitVectorSlide1Down"]),
        ("VPERM2",
            ["Vperm2", "Perm2", "VectorPermute2", "Permute2"],
            ["InstructionsEnum.VPERM2", "IsaOpcodeValues.VPERM2", "OpcodeValues.VPERM2", "CompileVperm2", "EmitVperm2", "CompileVectorPermute2", "EmitVectorPermute2"]),
        ("VTRANSPOSE",
            ["Vtranspose", "VectorTranspose", "Transpose"],
            ["InstructionsEnum.VTRANSPOSE", "IsaOpcodeValues.VTRANSPOSE", "OpcodeValues.VTRANSPOSE", "CompileVtranspose", "EmitVtranspose", "CompileVectorTranspose", "EmitVectorTranspose"])
    ];

    [Fact]
    public void PublicFacadeSurfaces_DoNotExposeClosedAtomicFenceMatrixCacheOrTlbHelpers()
    {
        string[] publicFacadeMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade))
        ];

        foreach (string methodName in publicFacadeMethods.Distinct(StringComparer.Ordinal))
        {
            if (CompilerMatrixTilePositiveEmissionAbiContract.PublicHelperNames.Contains(methodName))
            {
                continue;
            }

            Assert.DoesNotContain(
                ClosedHelperNameFragments,
                fragment => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void UnaryBitCountCompilerContours_DoNotOpenPopcntAliasOrSelectRows()
    {
        string[] publicFacadeMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade))
        ];

        Assert.Contains(nameof(IAppAsmFacade.CountTrailingZeros), publicFacadeMethods);
        Assert.Contains(nameof(AppAsmFacade.CountTrailingZeros), publicFacadeMethods);
        Assert.Contains(nameof(IAppAsmFacade.CountSetBits), publicFacadeMethods);
        Assert.Contains(nameof(AppAsmFacade.CountSetBits), publicFacadeMethods);

        string[] forbiddenPublicFragments =
        [
            "Popcnt",
            "PopulationCount",
            "CountPopulation",
            "Seqz",
            "Snez",
            "Csel"
        ];
        foreach (string fragment in forbiddenPublicFragments)
        {
            Assert.DoesNotContain(
                publicFacadeMethods,
                methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        string compilerSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();
        string[] forbiddenCompilerFragments =
        [
            "POPCNT",
            "CountPopulation",
            "PopulationCount",
            "SEQZ",
            "SNEZ",
            "InstructionsEnum.CSEL",
            "IsaOpcodeValues.CSEL",
            "OpcodeValues.CSEL",
            "CompileCsel",
            "EmitCsel"
        ];
        foreach (string fragment in forbiddenCompilerFragments)
        {
            Assert.DoesNotContain(fragment, compilerSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PlatformVectorFacade_RemainsRawTransportPlusScopedVsetvliOnly()
    {
        string[] platformMethods = PublicMethodNames(typeof(IPlatformAsmFacade));

        Assert.Contains(nameof(IPlatformAsmFacade.VectorOp), platformMethods);
        Assert.Contains(nameof(IPlatformAsmFacade.VectorOpImm), platformMethods);
        Assert.Contains(nameof(IPlatformAsmFacade.VSetVli), platformMethods);

        MethodInfo vectorOp = typeof(IPlatformAsmFacade).GetMethod(nameof(IPlatformAsmFacade.VectorOp))
            ?? throw new InvalidOperationException("IPlatformAsmFacade.VectorOp was not found.");
        MethodInfo vectorOpImm = typeof(IPlatformAsmFacade).GetMethod(nameof(IPlatformAsmFacade.VectorOpImm))
            ?? throw new InvalidOperationException("IPlatformAsmFacade.VectorOpImm was not found.");
        Assert.Equal(typeof(IsaOpcode), vectorOp.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(IsaOpcode), vectorOpImm.GetParameters()[0].ParameterType);

        Assert.DoesNotContain(platformMethods, name => name.Contains("Gather", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Scatter", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Indexed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("2D", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("VectorLoad", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("VectorStore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("MaskPrefix", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Vmsbf", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Select", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Merge", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Vzext", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Widen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Narrow", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platformMethods, name => name.Contains("Convert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThreadCompilerPublicSurface_KeepsLane6AndLane7DescriptorCarrierBoundaries()
    {
        string[] threadMethods = PublicMethodNames(typeof(HybridCpuThreadCompilerContext));

        string[] lane6Methods = threadMethods
            .Where(name => name.Contains("DmaStreamCompute", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [nameof(HybridCpuThreadCompilerContext.CompileDmaStreamCompute),
             nameof(HybridCpuThreadCompilerContext.CompileDmaStreamComputeDescriptor)],
            lane6Methods);

        string[] lane7Methods = threadMethods
            .Where(name => name.Contains("Accelerator", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([nameof(HybridCpuThreadCompilerContext.CompileAcceleratorSubmit)], lane7Methods);

        Assert.DoesNotContain(threadMethods, name => name.Contains("Production", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("Dsc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("DmaStreamStatus", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("DmaStreamQuery", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("DmaStreamQueue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("DmaStreamToken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("DmaStreamFence", StringComparison.OrdinalIgnoreCase));
        string[] closedLane7ThreadFragments =
        [
            "AcceleratorQuery",
            "AcceleratorPoll",
            "AcceleratorWait",
            "AcceleratorCancel",
            "AcceleratorFence",
            "AcceleratorStatus",
            "AcceleratorResult",
            "AcceleratorReset",
            "AcceleratorBackend",
            "AcceleratorFallback",
            "AcceleratorToken",
            "AcceleratorHandle",
            "AcceleratorTensor",
            "AcceleratorTopology",
            "AcceleratorFft",
            "AcceleratorCrypto",
            "AcceleratorHash",
            "AcceleratorSparse",
            "AcceleratorGraph"
        ];
        foreach (string fragment in closedLane7ThreadFragments)
        {
            Assert.DoesNotContain(
                threadMethods,
                name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(threadMethods, name => name.Contains("Fallback", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("Backend", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(threadMethods, name => name.Contains("Execute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeOnlyVectorContours_DoNotPublishTypedCompilerHelperBackendOrFallbackSurface()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];

        foreach ((string contourName, string[] publicHelperFragments, _) in RuntimeOnlyVectorContours)
        {
            foreach (string fragment in publicHelperFragments)
            {
                Assert.DoesNotContain(
                    publicCompilerMethods,
                    methodName =>
                        !CompilerMatrixTilePositiveEmissionAbiContract.PublicHelperNames.Contains(methodName) &&
                        methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }

            Assert.DoesNotContain(
                publicCompilerMethods,
                methodName =>
                    !CompilerMatrixTilePositiveEmissionAbiContract.PublicHelperNames.Contains(methodName) &&
                    (methodName.Contains($"{contourName}Backend", StringComparison.OrdinalIgnoreCase) ||
                     methodName.Contains($"{contourName}Fallback", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void CompilerSources_DoNotReferenceRuntimeOnlyVectorContoursAsLoweringAuthority()
    {
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();

        foreach ((_, _, string[] compilerSourceFragments) in RuntimeOnlyVectorContours)
        {
            foreach (string fragment in compilerSourceFragments)
            {
                Assert.DoesNotContain(
                    fragment,
                    compilerSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PlanningOnlyFixedPointSaturationRows_DoNotPublishTypedCompilerHelpersOrLoweringAuthority()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];
        string compilerEmissionSurfaceSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();

        foreach (CompilerFailClosedEmissionRow contour in CompilerFailClosedEmissionInventory.VectorFixedPointSaturationRows)
        {
            CompilerVectorVlmBlockedAbiContract contract = Assert.Single(
                CompilerVectorVlmBlockedAbiContract.AllVlmBlockedRows,
                row => row.Mnemonic == contour.Mnemonic);
            Assert.True(contract.IsFixedPointSaturation);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.TypedFacadeAllowed);
            Assert.False(contract.TypedHelperAllowed);
            Assert.True(contract.RequiresSignednessWidthClampPolicy);
            Assert.True(contract.RequiresStagedPublicationRetirePolicy);
            Assert.True(contract.RequiresReplayRollbackGoldenEvidence);
            Assert.True(contract.NoVaddSatFallback);
            Assert.True(contract.NoBaseVectorArithmeticFallback);
            Assert.True(contract.NoBaseVectorShiftFallback);
            Assert.True(contract.NoScalarHelperFallback);
            Assert.True(contract.NoLane6StreamFallback);
            Assert.True(contract.NoLane7AcceleratorFallback);
            Assert.True(contract.NoVmxSpecificPathFallback);
            Assert.True(contract.NoExecutableRowAliasPromotion);

            foreach (string fragment in contour.PublicHelperFragments)
            {
                Assert.DoesNotContain(
                    publicCompilerMethods,
                    methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string fragment in contour.CompilerSourceFragments)
            {
                Assert.DoesNotContain(
                    fragment,
                    compilerEmissionSurfaceSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PlanningOnlyFixedPointAverageClipRows_DoNotPublishTypedCompilerHelpersOrLoweringAuthority()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];
        string compilerEmissionSurfaceSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();

        foreach (CompilerFailClosedEmissionRow contour in CompilerFailClosedEmissionInventory.VectorFixedPointAverageClipRows)
        {
            CompilerVectorVlmBlockedAbiContract contract = Assert.Single(
                CompilerVectorVlmBlockedAbiContract.AllVlmBlockedRows,
                row => row.Mnemonic == contour.Mnemonic);
            Assert.True(contract.IsFixedPointAverageClip);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.TypedFacadeAllowed);
            Assert.False(contract.TypedHelperAllowed);
            Assert.True(contract.RequiresRoundingTruncationPolicyAbi);
            Assert.True(contract.RequiresStagedPublicationRetirePolicy);
            Assert.True(contract.RequiresReplayRollbackGoldenEvidence);
            Assert.True(contract.NoVaddSatFallback);
            Assert.True(contract.NoFixedPointSaturationFallback);
            Assert.True(contract.NoBaseVectorArithmeticFallback);
            Assert.True(contract.NoBaseVectorShiftFallback);
            Assert.True(contract.NoNarrowWidenConvertFallback);
            Assert.True(contract.NoScalarHelperFallback);
            Assert.True(contract.NoLane6StreamFallback);
            Assert.True(contract.NoLane7AcceleratorFallback);
            Assert.True(contract.NoVmxSpecificPathFallback);
            Assert.True(contract.NoExecutableRowAliasPromotion);

            foreach (string fragment in contour.PublicHelperFragments)
            {
                Assert.DoesNotContain(
                    publicCompilerMethods,
                    methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string fragment in contour.CompilerSourceFragments)
            {
                Assert.DoesNotContain(
                    fragment,
                    compilerEmissionSurfaceSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PlanningOnlyDotTileVariantRows_DoNotPublishTypedCompilerHelpersOrLoweringAuthority()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];
        string compilerEmissionSurfaceSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();

        foreach (CompilerFailClosedEmissionRow contour in CompilerFailClosedEmissionInventory.VectorDotTileVariantRows)
        {
            CompilerVectorVlmBlockedAbiContract contract = Assert.Single(
                CompilerVectorVlmBlockedAbiContract.AllVlmBlockedRows,
                row => row.Mnemonic == contour.Mnemonic);
            Assert.True(contract.IsDotTileVariant);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.TypedFacadeAllowed);
            Assert.False(contract.TypedHelperAllowed);
            Assert.True(contract.RequiresDotVariantAbi);
            Assert.True(contract.RequiresDotTileHelperAbi);
            Assert.True(contract.RequiresAccumulatorResultFootprintAbi);
            Assert.True(contract.RequiresDeterministicOrderingReplayPolicy);
            Assert.True(contract.RequiresStagedPublicationRetirePolicy);
            Assert.True(contract.RequiresReplayRollbackGoldenEvidence);
            Assert.True(contract.NoScopedVdotWideFallback);
            Assert.True(contract.NoNameOnlyVdotWideExtension);
            Assert.True(contract.NoBaseDotProductFallback);
            Assert.True(contract.NoWideningFmaFallback);
            Assert.True(contract.NoLane6DescriptorFallback);
            Assert.True(contract.NoMatrixTileFallback);
            Assert.True(contract.NoScalarHelperFallback);
            Assert.True(contract.NoLane6StreamFallback);
            Assert.True(contract.NoLane7AcceleratorFallback);
            Assert.True(contract.NoVmxSpecificPathFallback);
            Assert.True(contract.NoExecutableRowAliasPromotion);
            Assert.True(contract.NoHostOwnedEvidencePublication);

            foreach (string fragment in contour.PublicHelperFragments)
            {
                Assert.DoesNotContain(
                    publicCompilerMethods,
                    methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string fragment in contour.CompilerSourceFragments)
            {
                Assert.DoesNotContain(
                    fragment,
                    compilerEmissionSurfaceSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MatrixTileRuntimePromotion_OpensOnlyExplicitCompilerOwnedMatrixTileEmission()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string compilerEmissionSurfaceSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();

        Assert.Empty(CompilerFailClosedEmissionInventory.MatrixTileOptionalDisabledRows);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.HasCurrentCompilerImplementation);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.HasCurrentCompilerHelper);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.HasCurrentCompilerEmission);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.UsesPhase13RuntimeHandoff);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.RuntimeOwnedLegalityIsFinal);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.AllowsCompilerToOverrideRuntimeLegality);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesOldOptionalDisabledMetadataAsAuthority);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesFallbackPath);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesAliasPromotion);

        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.CompilerPositiveEmissionDecision,
            compilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.RuntimeHandoffAuthorityDecision,
            compilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.LegacyOptionalDisabledBoundaryDecision,
            compilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.NoFallbackDecision,
            compilerSource,
            StringComparison.Ordinal);

        foreach (CompilerMatrixTilePositiveEmissionRow row in CompilerMatrixTilePositiveEmissionAbiContract.Rows)
        {
            InstructionSupportStatus runtimeStatus = InstructionSupportStatusCatalog.GetStatus(row.Mnemonic);
            Assert.Equal(IsaInstructionStatus.OptionalEnabled, runtimeStatus.Status);
            Assert.Equal(RuntimeInstructionEvidence.ConformanceTested, runtimeStatus.RuntimeEvidence);
            Assert.True(runtimeStatus.HasRuntimeOpcodeMetadata);
            Assert.True(runtimeStatus.HasCanonicalDecoderAcceptance);
            Assert.True(runtimeStatus.HasRegistryFactory);
            Assert.True(runtimeStatus.HasExecutionSemantics);
            Assert.True(runtimeStatus.IsExecutableClaim);
            Assert.Equal(row.Opcode, (HybridCpuOpcode)(uint)Enum.Parse<InstructionsEnum>(row.Mnemonic));
            Assert.Equal((ushort)row.Opcode, row.NumericOpcode);
            Assert.True(row.UsesPhase13RuntimeHandoff);
            Assert.True(row.RuntimeOwnedLegalityIsFinal);
            Assert.True(row.EmitsDirectMatrixTileOpcode);
            Assert.False(row.UsesFallbackPath);
            Assert.False(row.UsesAliasPromotion);
            Assert.False(string.IsNullOrWhiteSpace(row.RequiredTypedOperandContract));

            CompilerMatrixTilePositiveEmissionAbiContract.RequireRuntimeHandoffAuthority(row.Mnemonic);
            Assert.Contains(row.HelperName, publicCompilerMethods);
            Assert.Contains(row.HelperName, compilerEmissionSurfaceSource, StringComparison.Ordinal);
            Assert.Contains($"HybridCpuOpcode.{row.Opcode}", compilerEmissionSurfaceSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RuntimeOnlyLane6DescriptorContours_DoNotPublishTypedCompilerHelpersOrLoweringAuthority()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];
        string compilerSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();

        foreach (CompilerFailClosedEmissionRow contour in CompilerFailClosedEmissionInventory.Lane6DescriptorRows
                     .Concat(CompilerFailClosedEmissionInventory.Lane6ShapeRows))
        {
            foreach (string fragment in contour.PublicHelperFragments)
            {
                Assert.DoesNotContain(
                    publicCompilerMethods,
                    methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string fragment in contour.CompilerSourceFragments)
            {
                Assert.DoesNotContain(
                    fragment,
                    compilerSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RuntimeOnlyLane6QueueQueryDsc2Contours_DoNotPublishTypedCompilerHelpersOrLoweringAuthority()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();

        foreach (CompilerFailClosedEmissionRow contour in CompilerFailClosedEmissionInventory.Lane6QueueQueryDsc2Rows)
        {
            foreach (string fragment in contour.PublicHelperFragments)
            {
                Assert.DoesNotContain(
                    publicCompilerMethods,
                    methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string fragment in contour.CompilerSourceFragments)
            {
                Assert.DoesNotContain(
                    fragment,
                    compilerSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void CompilerSources_DoNotReferenceLane7RuntimeOnlyTaxonomyOrBackendSurfacesAsAuthority()
    {
        string compilerSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();
        string[] forbiddenFragments =
        [
            .. CompilerFailClosedEmissionInventory.Lane7AcceleratorControlCompilerSourceFragments,
            "ACCEL_FFT",
            "ACCEL_CRYPTO",
            "ACCEL_HASH",
            "ACCEL_SPARSE",
            "ACCEL_GRAPH",
            "ExternalAcceleratorRuntime",
            "AcceleratorTokenStore",
            "AcceleratorCommandQueue",
            "AcceleratorFenceCoordinator",
            "AcceleratorRegisterAbi",
            "IExternalAcceleratorBackend",
            "FakeMatMulExternalAcceleratorBackend",
            "TensorMetadata",
            "TensorContract",
            "FftMetadata",
            "CryptoHashMetadata",
            "SparseGraphMetadata"
        ];

        foreach (string forbiddenFragment in forbiddenFragments)
        {
            Assert.DoesNotContain(
                forbiddenFragment,
                compilerSource,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VmxCompilerAuthorityMatrix_MarksRuntimeOpcodesAsRawTransportOnly()
    {
        InstructionsEnum[] expectedRuntimeVmxOpcodes =
        [
            InstructionsEnum.VMXON,
            InstructionsEnum.VMXOFF,
            InstructionsEnum.VMLAUNCH,
            InstructionsEnum.VMRESUME,
            InstructionsEnum.VMREAD,
            InstructionsEnum.VMWRITE,
            InstructionsEnum.VMCLEAR,
            InstructionsEnum.VMPTRLD,
            InstructionsEnum.VMPTRST,
            InstructionsEnum.VMCALL,
            InstructionsEnum.INVEPT,
            InstructionsEnum.INVVPID,
            InstructionsEnum.VMFUNC,
            InstructionsEnum.VMSAVEX,
            InstructionsEnum.VMRESTX,
        ];

        CompilerVmxOpcodeAuthority[] authority =
            [.. CompilerVmxAuthority.GetRuntimeOpcodeAuthority()];

        Assert.Equal(
            expectedRuntimeVmxOpcodes,
            authority.Select(static item => item.Opcode).ToArray());

        foreach (CompilerVmxOpcodeAuthority opcodeAuthority in authority)
        {
            Assert.Equal(CompilerVmxAuthorityKind.RuntimeExecutable, opcodeAuthority.Authority);
            Assert.True(opcodeAuthority.RuntimeExecutable);
            Assert.True(opcodeAuthority.RawTransportOnly);
            Assert.True(opcodeAuthority.RootRuntimePolicyGated);
            Assert.False(opcodeAuthority.CompilerHelperEmittable);
        }
    }

    [Fact]
    public void VmcsV2CompilerAuthority_SeparatesGuestVisibleFieldsRootPolicyAndHostEvidence()
    {
        VmcsV2BlockDirectory directory = VmcsV2BlockDirectory.CreateDefault();

        Assert.True(directory.TryGetField(
            (ushort)VmcsField.GuestPc,
            out VmcsV2FieldDescriptor guestPc));
        CompilerVmcsV2FieldAuthority guestPcAuthority =
            CompilerVmxAuthority.ClassifyVmcsV2Field(guestPc);
        Assert.Equal(CompilerVmxAuthorityKind.GuestVmreadVmwriteVisible, guestPcAuthority.Authority);
        Assert.True(guestPcAuthority.IsVmReadVisible);
        Assert.True(guestPcAuthority.IsVmWriteVisible);
        Assert.False(guestPcAuthority.ContainsHostEvidence);
        Assert.False(guestPcAuthority.CanAttachToExecutableCompilerInstruction);

        Assert.True(directory.TryGetField(
            VmcsV2BlockDirectory.ShadowVmcsBlockFieldId,
            out VmcsV2FieldDescriptor shadowVmcs));
        CompilerVmcsV2FieldAuthority shadowAuthority =
            CompilerVmxAuthority.ClassifyVmcsV2Field(shadowVmcs);
        Assert.Equal(CompilerVmxAuthorityKind.HostEvidenceOnly, shadowAuthority.Authority);
        Assert.False(shadowAuthority.IsVmReadVisible);
        Assert.False(shadowAuthority.IsVmWriteVisible);
        Assert.True(shadowAuthority.ContainsHostEvidence);
        Assert.False(shadowAuthority.CanAttachToExecutableCompilerInstruction);

        Assert.True(directory.TryGetField(
            VmcsV2BlockDirectory.DirtyLogBlockFieldId,
            out VmcsV2FieldDescriptor dirtyLog));
        CompilerVmcsV2FieldAuthority dirtyLogAuthority =
            CompilerVmxAuthority.ClassifyVmcsV2Field(dirtyLog);
        Assert.Equal(CompilerVmxAuthorityKind.RootRuntimeApiOnly, dirtyLogAuthority.Authority);
        Assert.False(dirtyLogAuthority.IsVmReadVisible);
        Assert.False(dirtyLogAuthority.IsVmWriteVisible);
        Assert.False(dirtyLogAuthority.CanAttachToExecutableCompilerInstruction);

        Assert.True(CompilerVmxAuthority.TryCreateVmcsV2ValidationSideband(
            vmcsV2Revision: 1,
            dirtyLog,
            out CompilerVmcsV2DescriptorSideband sideband,
            out string diagnostic));
        Assert.True(sideband.ValidationOnly);
        Assert.False(sideband.CanAttachToExecutableCompilerInstruction);
        Assert.Equal(string.Empty, diagnostic);
        Assert.False(CompilerVmxAuthority.CanAttachVmcsV2SidebandToOpcode(
            InstructionsEnum.VMREAD,
            sideband,
            out string attachDiagnostic));
        Assert.Contains("validation-only", attachDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VmxPreflightDiagnostics_FailClosedWhenRootPolicyIsDisabled()
    {
        CompilerVmxPreflightRequest request = new CompilerVmxPreflightRequest(
            RequiredVmcsV2Revision: 1,
            CompilerVmxRequestedFeature.NestedVmx |
            CompilerVmxRequestedFeature.Migration |
            CompilerVmxRequestedFeature.DirtyLogging |
            CompilerVmxRequestedFeature.ObservabilityExport);

        CompilerVmxTargetCapability capabilities =
            CompilerVmxTargetCapability.VmcsV2Revision1 |
            CompilerVmxTargetCapability.NestedVmx |
            CompilerVmxTargetCapability.Migration |
            CompilerVmxTargetCapability.DirtyLogging |
            CompilerVmxTargetCapability.ObservabilityExport;

        CompilerVmxPreflightResult disabledPolicyResult =
            CompilerVmxAuthority.EvaluatePreflight(
                request,
                capabilities,
                CompilerVmxRootPolicy.Disabled);

        Assert.False(disabledPolicyResult.Succeeded);
        Assert.Equal(CompilerVmxPreflightRejectionKind.RootPolicyDisabled, disabledPolicyResult.RejectionKind);
        Assert.Equal(CompilerVmxRequestedFeature.NestedVmx, disabledPolicyResult.RejectedFeature);
        Assert.DoesNotContain("VMCS02", disabledPolicyResult.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VPID", disabledPolicyResult.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", disabledPolicyResult.Diagnostic, StringComparison.OrdinalIgnoreCase);

        CompilerVmxPreflightResult unsupportedCapabilityResult =
            CompilerVmxAuthority.EvaluatePreflight(
                request,
                CompilerVmxTargetCapability.VmcsV2Revision1,
                new CompilerVmxRootPolicy(
                    NestedVmxEnabled: true,
                    VectorStreamRestoreEnabled: false,
                    Lane6RestoreEnabled: false,
                    Lane7RestoreEnabled: false,
                    MigrationEnabled: true,
                    DirtyLoggingEnabled: true,
                    ObservabilityExportEnabled: true));

        Assert.False(unsupportedCapabilityResult.Succeeded);
        Assert.Equal(
            CompilerVmxPreflightRejectionKind.UnsupportedTargetCapability,
            unsupportedCapabilityResult.RejectionKind);
        Assert.Equal(CompilerVmxRequestedFeature.NestedVmx, unsupportedCapabilityResult.RejectedFeature);
    }

    [Fact]
    public void CompilerSources_DoNotPublishVmxPhase8GuestAbiOrHostEvidenceHelpers()
    {
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] forbiddenFragments =
        [
            "VMDIRTYLOG",
            "CompileVmx",
            "CompileVmcs",
            "CompileVmFunc",
            "CompileCheckpoint",
            "CompileMigration",
            "CompileDirtyLog",
            "CompileObservability",
            "CompileDebugTrace",
            "HostOwnedVmcs02Pointer",
            "HostOwnedVpid",
            "HostOwnedVmid",
            "IotlbCache",
            "NativeToken",
            "BackendBinding",
            "TraceHandle",
        ];

        foreach (string forbiddenFragment in forbiddenFragments)
        {
            Assert.DoesNotContain(
                forbiddenFragment,
                compilerSource,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SupportCatalogClosedRows_DoNotBecomeCompilerEmissionAuthority()
    {
        InstructionSupportStatus[] closedRows =
        [
            .. InstructionSupportStatusCatalog.ExplicitStatuses
                .Where(static status => IsNoEmissionStatus(status.Status)),
            .. IsaV4Surface.ProhibitedOpcodes.Select(InstructionSupportStatusCatalog.GetStatus),
            .. IsaV4Surface.ReservedOpcodes.Select(InstructionSupportStatusCatalog.GetStatus),
            .. IsaV4Surface.OptionalDisabledOpcodes.Select(InstructionSupportStatusCatalog.GetStatus),
            .. IsaV4Surface.ParserOnlyOpcodes.Select(InstructionSupportStatusCatalog.GetStatus),
            .. IsaV4Surface.DescriptorOnlyOpcodes.Select(InstructionSupportStatusCatalog.GetStatus),
            .. IsaV4Surface.CarrierOnlyOpcodes.Select(InstructionSupportStatusCatalog.GetStatus)
        ];

        closedRows = closedRows
            .GroupBy(static status => status.Mnemonic, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static status => status.Mnemonic, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(closedRows);

        foreach (InstructionSupportStatus status in closedRows)
        {
            Assert.True(
                IsNoEmissionStatus(status.Status),
                $"{status.Mnemonic} must stay in a no-emission status.");
            Assert.False(
                status.IsExecutableClaim,
                $"{status.Mnemonic} metadata/catalog presence must not grant execution authority.");
            Assert.False(
                status.HasExecutionSemantics,
                $"{status.Mnemonic} must not publish compiler/runtime execution semantics while closed.");
        }
    }

    [Fact]
    public void ThreadCompilerIngress_RejectsStreamLengthOverflowBeforeCarrierEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        ulong overflowingStreamLength = (ulong)uint.MaxValue + 1UL;

        ArgumentOutOfRangeException compileException = Assert.Throws<ArgumentOutOfRangeException>(
            () => context.CompileInstruction(
                (uint)InstructionsEnum.ADDI,
                (byte)DataTypeEnum.INT32,
                predicate: 0,
                immediate: 1,
                destSrc1: VLIW_Instruction.PackArchRegs(1, 2, 0),
                src2: 0,
                streamLength: overflowingStreamLength,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable));
        Assert.Equal("streamLength", compileException.ParamName);
        Assert.Equal(0, context.InstructionCount);

        ArgumentOutOfRangeException insertException = Assert.Throws<ArgumentOutOfRangeException>(
            () => context.InsertInstruction(
                instructionIndex: 0,
                opCode: (uint)InstructionsEnum.ADDI,
                dataType: (byte)DataTypeEnum.INT32,
                predicate: 0,
                immediate: 1,
                destSrc1: VLIW_Instruction.PackArchRegs(1, 2, 0),
                src2: 0,
                streamLength: overflowingStreamLength,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable));
        Assert.Equal("streamLength", insertException.ParamName);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void LegacyCompilerBridge_RejectsStreamLengthOverflowBeforeCarrierEmission()
    {
        ProcessorCompilerBridge bridge = new ProcessorCompilerBridge();
        bridge.DeclareCompilerContractVersion(
            CompilerContract.Version,
            producerSurface: "vliw-bundle-audit-test");

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => bridge.Add_VLIW_Instruction(
                opCode: (uint)InstructionsEnum.ADDI,
                dataType: (byte)DataTypeEnum.INT32,
                predicate: 0,
                immediate: 1,
                destSrc1: VLIW_Instruction.PackArchRegs(1, 2, 0),
                src2: 0,
                streamLength: (ulong)uint.MaxValue + 1UL,
                stride: 0));

        Assert.Equal("streamLength", exception.ParamName);
        Assert.Equal(0, bridge.InstructionCount);
    }

    private static string[] PublicMethodNames(Type type)
    {
        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .ToArray();
    }

    private static bool IsNoEmissionStatus(IsaInstructionStatus status) =>
        status is
            IsaInstructionStatus.OptionalDisabled or
            IsaInstructionStatus.Reserved or
            IsaInstructionStatus.LegacyRetained or
            IsaInstructionStatus.ParserOnly or
            IsaInstructionStatus.DescriptorOnly or
            IsaInstructionStatus.Prohibited or
            IsaInstructionStatus.CarrierOnly;
}
