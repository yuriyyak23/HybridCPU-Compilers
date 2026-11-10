using System.Reflection;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Llvm;
using LLVMSharp.Interop;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase10LlvmSemanticMappingTests
{
    [Theory]
    [InlineData(8, -1L, 0, false)]
    [InlineData(8, -1L, 1, false)]
    [InlineData(8, -128L, 7, false)]
    [InlineData(8, -1L, 7, true)]
    [InlineData(16, -1L, 0, false)]
    [InlineData(16, -1L, 15, false)]
    [InlineData(16, -32768L, 1, true)]
    [InlineData(16, 32767L, 15, true)]
    [InlineData(32, -1L, 0, false)]
    [InlineData(32, -1L, 31, false)]
    [InlineData(32, -2147483648L, 1, true)]
    [InlineData(32, 2147483647L, 31, true)]
    public void RealLlvmNarrowShl_ExplicitNormalizationReachesEncodedConsumer(int width, long value, int count, bool ssa)
    {
        if (!PinnedRuntimeAvailable()) return;
        string literal = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string first = ssa ? $"xor i{width} {literal}, 3" : $"shl i{width} {literal}, {count}";
        string second = ssa ? $"shl i{width} %sum, {count}" : $"xor i{width} %sum, 1";
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", first, StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", second, StringComparison.Ordinal);
        if (ssa) text = text.Replace("ret void", $"%observed = xor i{width} %product, 1\n  ret void", StringComparison.Ordinal);
        string root = ssa ? "llvm:kernel:bb0:i1" : "llvm:kernel:bb0:i0";
        byte[] Compile()
        {
            IrProgram program = Assert.IsType<IrProgram>(MapText(text).Program);
            IrInstruction result = Assert.Single(program.Instructions, instruction => instruction.StableIdentity == root);
            Assert.Equal(HybridCpuOpcode.SRLI, result.Opcode);
            Assert.Equal(width, result.CanonicalType.BitWidth);
            Assert.Equal((ushort)(64 - width), result.Immediate);
            Assert.Equal(HybridCpuDataType.UINT64, result.DataType);
            IrInstruction wide = Assert.Single(program.Instructions, instruction => instruction.StableIdentity == root + ":wide-shift");
            Assert.Equal(HybridCpuOpcode.SLLI, wide.Opcode);
            Assert.Equal((ushort)count, wide.Immediate);
            Assert.Equal(64, wide.CanonicalType.BitWidth);
            Assert.Equal(result.OriginChain, wide.OriginChain);
            Assert.Equal(width, Assert.Single(program.ValueFlow.Values,
                v => v.StableId == result.Annotation.Defs[0].Name).ValueKind.BitWidth);
            IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
            IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
            HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
                new HybridCPU.Compiler.Core.IR.Resources.HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
            IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
                resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
            Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
            Assert.NotNull(allocation.Witness);
            HybridCpuCompiledProgram compiled = HybridCpuIrProgramCompiler.CompileAllocated(allocation);
            ulong[] registers = new ulong[32];
            int xors = 0, normalizations = 0;
            ulong mask = (1UL << width) - 1;
            ulong expected = (((unchecked((ulong)value) ^ (ssa ? 3UL : 0UL)) << count) & mask) ^ 1UL;
            for (int offset = 0; offset < compiled.ProgramImage.Length; offset += 32)
            {
                HybridCpuInstructionWord word = new HybridCpuInstructionWord();
                Assert.True(word.TryReadBytes(compiled.ProgramImage, offset));
                HybridCpuInstructionWord lowered = compiled.LoweredBundles[offset / 256].GetInstruction(offset / 32 % 8);
                Assert.Equal((lowered.Word0, lowered.Word1, lowered.Word2, lowered.Word3),
                    (word.Word0, word.Word1, word.Word2, word.Word3));
                if (word.OpCode is not ((uint)HybridCpuOpcode.ADDI or (uint)HybridCpuOpcode.ORI or
                    (uint)HybridCpuOpcode.SLLI or (uint)HybridCpuOpcode.SRLI or (uint)HybridCpuOpcode.XOR)) continue;
                int rd = (int)(word.Word1 & 255), rs1 = (int)((word.Word1 >> 16) & 255),
                    rs2 = (int)((word.Word1 >> 32) & 255);
                Assert.InRange(rd, 0, 31); Assert.InRange(rs1, 0, 31);
                if (word.OpCode == (uint)HybridCpuOpcode.ADDI) registers[rd] = unchecked(registers[rs1] + (ulong)(long)(short)word.Immediate);
                if (word.OpCode == (uint)HybridCpuOpcode.ORI) registers[rd] = registers[rs1] | unchecked((ulong)(long)(short)word.Immediate);
                if (word.OpCode == (uint)HybridCpuOpcode.SLLI) registers[rd] = registers[rs1] << (word.Immediate & 63);
                if (word.OpCode == (uint)HybridCpuOpcode.SRLI)
                {
                    Assert.Equal(HybridCpuDataType.UINT64, word.DataTypeValue);
                    Assert.Equal(64 - width, word.Immediate);
                    registers[rd] = registers[rs1] >> (word.Immediate & 63);
                    Assert.InRange(registers[rd], 0UL, mask); normalizations++;
                }
                if (word.OpCode != (uint)HybridCpuOpcode.XOR) continue;
                Assert.InRange(rs2, 0, 31);
                registers[rd] = registers[rs1] ^ registers[rs2];
                if (++xors == (ssa ? 2 : 1)) Assert.Equal(expected, registers[rd]);
            }
            Assert.Equal(ssa ? 2 : 1, xors); Assert.Equal(1, normalizations);
            return compiled.ProgramImage;
        }
        Assert.Equal(Compile(), Compile());
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void LlvmNarrowShl_SourceWidthCountAndFlagsRemainFailClosed(int width)
    {
        if (!PinnedRuntimeAvailable()) return;
        string Text(string operation) => SupportedModuleText.Replace("add nsw i32 1, 2", operation, StringComparison.Ordinal)
            .Replace("  %product = mul i32 %sum, 4\n", "", StringComparison.Ordinal);
        foreach (int count in new[] { width, width + 1, -1 })
        {
            LlvmSemanticMappingResultV1 result = MapText(Text($"shl i{width} 5, {count}"));
            Assert.Null(result.Program); Assert.Equal("HCLM0036", Assert.Single(result.Diagnostics).Code);
        }
        foreach (string flag in new[] { "nuw", "nsw", "nuw nsw" })
        {
            LlvmSemanticMappingResultV1 result = MapText(Text($"shl {flag} i{width} 5, 1"));
            Assert.Null(result.Program); Assert.Equal("HCLM0035", Assert.Single(result.Diagnostics).Code);
        }
    }

    [Fact]
    public void LlvmNarrowShl_NormalizationCannotOverrunRemainingInstructionBudget()
    {
        if (!PinnedRuntimeAvailable()) return;
        int limit = LlvmToolchainContractV1.Default.Limits.MaximumInstructions;
        const int value = unchecked((int)0x89abcdef);
        int cost = HybridCpuConstantMaterializationPlanV1.Create(unchecked((ulong)(long)value)).Count + 3;
        int rows = (limit - 3) / cost;
        int padding = limit - 2 - rows * cost;
        StringBuilder text = new StringBuilder("""
            target datalayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64"
            target triple = "hybridcpuv2-unknown-none"
            define void @kernel() {
            entry:
            """ + "\n");
        for (int index = 0; index < rows; index++) text.AppendLine($" %v{index} = shl i32 {value}, 0");
        for (int index = 0; index < padding; index++) text.AppendLine($" %p{index} = xor i32 %v0, %v0");
        text.AppendLine(" %last = shl i32 %v0, 0\n ret void\n}\n!llvm.ident = !{!0}\n!0 = !{!\"LLVM 20.1.2\"}");
        Assert.True(rows + padding + 2 < limit);
        LlvmSemanticMappingResultV1 result = MapText(text.ToString());
        Assert.Equal(LlvmSemanticMappingStatusV1.BudgetExhausted, result.Status);
        Assert.Null(result.Program);
        IrFrontendDiagnosticV1 diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("HCLM0033", diagnostic.Code);
        Assert.Equal($"llvm:kernel:bb0:i{rows + padding}", diagnostic.StableSourceIdentity);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(5L, 1)]
    [InlineData(32768L, 8)]
    [InlineData(-32769L, 31)]
    [InlineData(1L, 32)]
    [InlineData(1L, 63)]
    [InlineData(long.MinValue, 0)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(unchecked((long)0x89abcdef12345678UL), 32)]
    public void RealLlvmShlI64_ConstantCountReachesEncodedScalar(long value, int count)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2",
            $"shl i64 {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {count}", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", "xor i64 %sum, 1", StringComparison.Ordinal);
        byte[] Compile()
        {
            IrProgram program = Assert.IsType<IrProgram>(MapText(text).Program);
            IrInstruction shift = Assert.Single(program.Instructions, i => i.StableIdentity == "llvm:kernel:bb0:i0");
            Assert.Equal(HybridCpuOpcode.SLLI, shift.Opcode);
            Assert.Equal((ushort)count, shift.Immediate);
            Assert.Equal(IrOperandKind.Immediate, shift.Annotation.Uses[1].Kind);
            Assert.Equal((ulong)count, shift.Annotation.Uses[1].Value);
            Assert.Equal(IrOperandKind.VirtualValue, shift.Annotation.Uses[0].Kind);
            Assert.Equal(IrShiftSemantics.SourcePoisonOutOfRange, shift.Semantics.Shift);
            IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
            IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
            HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
                new HybridCPU.Compiler.Core.IR.Resources.HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
            IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
                resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
            Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
            Assert.NotNull(allocation.Witness);
            HybridCpuCompiledProgram compiled = HybridCpuIrProgramCompiler.CompileAllocated(allocation);
            ulong[] registers = new ulong[32];
            int consumers = 0;
            for (int offset = 0; offset < compiled.ProgramImage.Length; offset += 32)
            {
                HybridCpuInstructionWord word = new HybridCpuInstructionWord();
                Assert.True(word.TryReadBytes(compiled.ProgramImage, offset));
                HybridCpuInstructionWord lowered = compiled.LoweredBundles[offset / 256].GetInstruction(offset / 32 % 8);
                Assert.Equal((lowered.Word0, lowered.Word1, lowered.Word2, lowered.Word3),
                    (word.Word0, word.Word1, word.Word2, word.Word3));
                int rd = (int)(word.Word1 & 255), rs1 = (int)((word.Word1 >> 16) & 255),
                    rs2 = (int)((word.Word1 >> 32) & 255);
                if (word.OpCode is not ((uint)HybridCpuOpcode.ADDI or (uint)HybridCpuOpcode.ORI or
                    (uint)HybridCpuOpcode.SLLI or (uint)HybridCpuOpcode.XOR)) continue;
                Assert.InRange(rd, 0, 31); Assert.InRange(rs1, 0, 31);
                if (word.OpCode == (uint)HybridCpuOpcode.XOR) Assert.InRange(rs2, 0, 31);
                if (word.OpCode == (uint)HybridCpuOpcode.ADDI) registers[rd] = unchecked(registers[rs1] + (ulong)(long)(short)word.Immediate);
                if (word.OpCode == (uint)HybridCpuOpcode.ORI) registers[rd] = registers[rs1] | unchecked((ulong)(long)(short)word.Immediate);
                if (word.OpCode == (uint)HybridCpuOpcode.SLLI)
                {
                    Assert.Equal(HybridCpuDataType.UINT64, word.DataTypeValue);
                    registers[rd] = registers[rs1] << (word.Immediate & 63);
                }
                if (word.OpCode != (uint)HybridCpuOpcode.XOR) continue;
                registers[rd] = registers[rs1] ^ registers[rs2];
                Assert.Equal((unchecked((ulong)value) << count) ^ 1UL, registers[rd]);
                consumers++;
            }
            Assert.Equal(1, consumers);
            return compiled.ProgramImage;
        }
        Assert.Equal(Compile(), Compile());
    }

    [Theory]
    [InlineData("shl nsw i64 5, 1", "HCLM0035", true, false)]
    [InlineData("shl nuw i64 5, 1", "HCLM0035", false, true)]
    [InlineData("shl nuw nsw i64 5, 1", "HCLM0035", true, true)]
    [InlineData("shl i64 5, 64", "HCLM0036", false, false)]
    [InlineData("shl i64 5, -1", "HCLM0036", false, false)]
    [InlineData("shl i32 5, 32", "HCLM0036", false, false)]
    public void LlvmShl_ObservedFlagsAndExcludedCountsFailClosed(string operation, string code, bool nsw, bool nuw)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", operation, StringComparison.Ordinal)
            .Replace("  %product = mul i32 %sum, 4\n", "", StringComparison.Ordinal);
        string path = CreateTempPath("ll"); File.WriteAllText(path, text);
        try
        {
            MethodInfo method = typeof(LlvmModuleImporterV1).GetMethod("ImportFileForSemanticMapping",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            LlvmImportResultV1 import = Assert.IsType<LlvmImportResultV1>(method.Invoke(new LlvmModuleImporterV1(), [path, LlvmInputKind.TextIr]));
            Assert.Equal(LlvmImportStatus.Success, import.Status);
            LlvmSemanticInstructionSnapshotV1 source = import.Module!.SemanticSnapshot!.Functions[0].Blocks[0].Instructions[0];
            Assert.Equal(nsw, source.NoSignedWrap); Assert.Equal(nuw, source.NoUnsignedWrap);
            LlvmSemanticMappingResultV1 first = new LlvmSemanticMapperV1().MapFile(path, LlvmInputKind.TextIr);
            LlvmSemanticMappingResultV1 second = new LlvmSemanticMapperV1().MapFile(path, LlvmInputKind.TextIr);
            Assert.Equal(LlvmSemanticMappingStatusV1.Unsupported, first.Status);
            Assert.Null(first.Program); Assert.Null(second.Program);
            Assert.Equal(code, Assert.Single(first.Diagnostics).Code);
            Assert.Equal(first.Diagnostics, second.Diagnostics);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LlvmShl_SsaValueAcceptedButSsaCountRejected()
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", "xor i64 5, 3", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", "shl i64 %sum, 1", StringComparison.Ordinal);
        Assert.NotNull(MapText(text).Program);
        LlvmSemanticMappingResultV1 dynamic = MapText(text.Replace("shl i64 %sum, 1", "shl i64 5, %sum", StringComparison.Ordinal));
        Assert.Null(dynamic.Program); Assert.Equal("HCLM0036", Assert.Single(dynamic.Diagnostics).Code);
        Assert.Null(MapText(text.Replace("shl i64 %sum, 1", "shl i64 undef, 1", StringComparison.Ordinal)).Program);
        Assert.Null(MapText(text.Replace("shl i64 %sum, 1", "shl i64 %sum, poison", StringComparison.Ordinal)).Program);
        string path = CreateTempPath("ll"); File.WriteAllText(path, text);
        try { Assert.NotEqual(LlvmImportStatus.Success, new LlvmModuleImporterV1().ImportFile(path, LlvmInputKind.TextIr).Status); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("", 5, false)]
    [InlineData("disjoint ", 5, true)]
    [InlineData("disjoint ", 4, true)]
    public void LlvmOr_SnapshotFlagIsObservedAndDisjointFailsClosed(string flag, int left, bool disjoint)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", $"or {flag}i32 {left}, 3", StringComparison.Ordinal);
        string path = CreateTempPath("ll"); File.WriteAllText(path, text);
        try
        {
            MethodInfo method = typeof(LlvmModuleImporterV1).GetMethod("ImportFileForSemanticMapping",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            LlvmImportResultV1 import = Assert.IsType<LlvmImportResultV1>(method.Invoke(new LlvmModuleImporterV1(), [path, LlvmInputKind.TextIr]));
            Assert.Equal(LlvmImportStatus.Success, import.Status);
            LlvmSemanticModuleSnapshotV1 snapshot = Assert.IsType<LlvmSemanticModuleSnapshotV1>(import.Module!.SemanticSnapshot);
            Assert.Equal("hybridcpu.llvm-semantic-snapshot/v3", snapshot.SchemaId);
            Assert.Equal(disjoint, snapshot.Functions[0].Blocks[0].Instructions[0].IsDisjoint);
            LlvmSemanticMappingResultV1 mapped = new LlvmSemanticMapperV1().MapFile(path, LlvmInputKind.TextIr);
            if (!disjoint) Assert.NotNull(mapped.Program);
            else
            {
                Assert.Null(mapped.Program);
                Assert.Contains(mapped.Diagnostics, diagnostic => diagnostic.Code == "HCLM0034");
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void RealLlvmConstantExpansion_EnforcesInclusiveInstructionBudget(int delta)
    {
        if (!PinnedRuntimeAvailable()) return;
        int limit = LlvmToolchainContractV1.Default.Limits.MaximumInstructions;
        const ulong value = 0x1122334455667788UL;
        int cost = HybridCpuConstantMaterializationPlanV1.Create(value).Count + 2;
        int rows = (limit - 1) / cost, baseCount = rows * cost + 1, expected = limit + delta;
        StringBuilder text = new StringBuilder("""
            target datalayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64"
            target triple = "hybridcpuv2-unknown-none"
            define void @kernel() {
            entry:
            """ + "\n");
        for (int index = 0; index < rows; index++)
            text.AppendLine($" %v{index} = xor i64 {value}, 3");
        for (int index = baseCount; index < expected; index++)
            text.AppendLine($" %p{index} = xor i64 %v0, %v0");
        text.AppendLine(" ret void\n}\n!llvm.ident = !{!0}\n!0 = !{!\"LLVM 20.1.2\"}");
        Assert.True(rows + expected - baseCount + 1 < limit, "Source instruction budget must not mask expansion admission.");
        LlvmSemanticMappingResultV1 result = MapText(text.ToString());
        if (delta <= 0)
        {
            Assert.Equal(LlvmSemanticMappingStatusV1.Success, result.Status);
            Assert.Equal(expected, Assert.IsType<IrProgram>(result.Program).Instructions.Count);
        }
        else
        {
            Assert.Equal(LlvmSemanticMappingStatusV1.BudgetExhausted, result.Status);
            Assert.Null(result.Program);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HCLM0033");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HCLL0013");
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(32767L)]
    [InlineData(32768L)]
    [InlineData(-32768L)]
    [InlineData(-32769L)]
    [InlineData(1099511627776L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(unchecked((long)0x89abcdef12345678UL))]
    public void RealLlvmI64Xor_EncodedPhysicalOperandsPreserveExactBitPattern(long constant)
        => AssertEncodedBitwise("xor", HybridCpuOpcode.XOR, 64, constant);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(-32768)]
    [InlineData(-32769)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(unchecked((int)0x89abcdef))]
    public void RealLlvmI32And_EncodedPhysicalOperandsPreserveExactBitPattern(int constant)
        => AssertEncodedBitwise("and", HybridCpuOpcode.AND, 32, constant);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(-32768)]
    [InlineData(-32769)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(unchecked((int)0x89abcdef))]
    public void RealLlvmI32Or_EncodedPhysicalOperandsPreserveExactBitPattern(int constant)
        => AssertEncodedBitwise("or", HybridCpuOpcode.OR, 32, constant);

    private static void AssertEncodedBitwise(string operation, HybridCpuOpcode opcode, int width, long constant)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2",
            $"{operation} i{width} {constant.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 3", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", $"{operation} i{width} %sum, 1", StringComparison.Ordinal);
        HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
            new HybridCPU.Compiler.Core.IR.Resources.HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
        HybridCpuCompiledProgram Compile()
        {
            IrProgram program = Assert.IsType<IrProgram>(MapText(text).Program);
            IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
            IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
            IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
                resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
            Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
            Assert.NotNull(allocation.Witness);
            return HybridCpuIrProgramCompiler.CompileAllocated(allocation);
        }
        HybridCpuCompiledProgram compiled = Compile();
        ulong[] registers = new ulong[32];
        int xors = 0;
        for (int offset = 0; offset < compiled.ProgramImage.Length; offset += 32)
        {
            HybridCpuInstructionWord word = new HybridCpuInstructionWord();
            Assert.True(word.TryReadBytes(compiled.ProgramImage, offset));
            HybridCpuInstructionWord lowered = compiled.LoweredBundles[offset / 256].GetInstruction(offset / 32 % 8);
            Assert.Equal((lowered.Word0, lowered.Word1, lowered.Word2, lowered.Word3),
                (word.Word0, word.Word1, word.Word2, word.Word3));
            int rd = (int)(word.Word1 & 255), rs1 = (int)((word.Word1 >> 16) & 255),
                rs2 = (int)((word.Word1 >> 32) & 255);
            if (word.OpCode == (uint)HybridCpuOpcode.ADDI) registers[rd] = unchecked(registers[rs1] + (ulong)(long)(short)word.Immediate);
            if (word.OpCode == (uint)HybridCpuOpcode.SLLI) registers[rd] = registers[rs1] << (word.Immediate & 63);
            if (word.OpCode == (uint)HybridCpuOpcode.ORI) registers[rd] = registers[rs1] | unchecked((ulong)(long)(short)word.Immediate);
            if (word.OpCode != (uint)opcode) continue;
            Assert.Equal(width == 64 ? HybridCpuDataType.UINT64 : HybridCpuDataType.UINT32, word.DataTypeValue);
            Assert.InRange(rd, 0, 31); Assert.InRange(rs1, 0, 31); Assert.InRange(rs2, 0, 31);
            registers[rd] = opcode switch { HybridCpuOpcode.AND => registers[rs1] & registers[rs2],
                HybridCpuOpcode.OR => registers[rs1] | registers[rs2], _ => registers[rs1] ^ registers[rs2] };
            if (width == 32) registers[rd] = (uint)registers[rd];
            xors++;
            ulong expected = opcode switch { HybridCpuOpcode.AND => unchecked((ulong)constant) & 3UL,
                HybridCpuOpcode.OR => unchecked((ulong)constant) | 3UL, _ => unchecked((ulong)constant) ^ 3UL };
            if (xors == 2) expected = opcode switch { HybridCpuOpcode.AND => expected & 1UL,
                HybridCpuOpcode.OR => expected | 1UL, _ => expected ^ 1UL };
            if (width == 32) expected = (uint)expected;
            Assert.Equal(expected, registers[rd]);
        }
        Assert.Equal(2, xors);
        Assert.Equal(compiled.ProgramImage, Compile().ProgramImage);
    }

    [Theory]
    [InlineData("and", HybridCpuOpcode.AND)]
    [InlineData("or", HybridCpuOpcode.OR)]
    public void LlvmBitwise_RawUnmaterializedConstantsRejectWithoutWitness(string operation, HybridCpuOpcode opcode)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", $"{operation} i32 5, 3", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", $"{operation} i32 %sum, 1", StringComparison.Ordinal);
        IrProgram program = Assert.IsType<IrProgram>(MapText(text).Program);
        IrInstruction first = program.Instructions.First(instruction => instruction.Opcode == opcode);
        IrOperand[] constants = [new(IrOperandKind.Constant, 5, "raw-five"), new(IrOperandKind.Constant, 3, "raw-three")];
        IrInstruction replaced = first with { Operands = [.. constants, .. first.Annotation.Defs],
            Annotation = first.Annotation with { Uses = constants } };
        IrInstruction[] instructions = program.Instructions.Select(instruction => instruction.Index == first.Index ? replaced : instruction).ToArray();
        IrProgram raw = program with {
            Instructions = instructions,
            ControlFlowGraph = new([program.BasicBlocks[0] with { Instructions = instructions }], program.ControlFlowGraph.Edges),
            ValueFlow = program.ValueFlow with { Accesses = program.ValueFlow.Accesses
                .Where(access => access.InstructionIndex != first.Index || access.Kind == IrValueAccessKind.Def).ToArray() }
        };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(raw);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
            new HybridCPU.Compiler.Core.IR.Resources.HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
            resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Unsupported, allocation.Status);
        Assert.Null(allocation.Witness);
        Assert.Same(schedule, allocation.FinalSchedule); Assert.Same(bundles, allocation.FinalBundles);
        string path = CreateTempPath("ll"); File.WriteAllText(path, text);
        try
        {
            LlvmImportResultV1 structural = new LlvmModuleImporterV1().ImportFile(path, LlvmInputKind.TextIr);
            Assert.Equal(LlvmImportStatus.Unsupported, structural.Status);
            Assert.Contains(structural.Diagnostics, diagnostic => diagnostic.Code == "HCLL0014");
        }
        finally { File.Delete(path); }
        Assert.Null(MapText(text.Replace("5, 3", "undef, 3", StringComparison.Ordinal)).Program);
    }

    [Theory]
    [InlineData(HybridCpuOpcode.SLLI)]
    [InlineData(HybridCpuOpcode.SRLI)]
    public void ShiftRawRegisterConstant_RejectsWithoutWitnessAndPreservesExactPath(HybridCpuOpcode opcode)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", "shl i64 5, 1", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", "xor i64 %sum, 1", StringComparison.Ordinal);
        IrProgram program = Assert.IsType<IrProgram>(MapText(text).Program);
        IrInstruction shift = Assert.Single(program.Instructions, instruction => instruction.StableIdentity == "llvm:kernel:bb0:i0");
        IrOperand[] uses = [new(IrOperandKind.Constant, 8, "raw-eight"), shift.Annotation.Uses[1]];
        IrInstruction replacement = shift with { Opcode = opcode, Operands = [.. uses, .. shift.Annotation.Defs],
            Annotation = shift.Annotation with { Uses = uses } };
        IrInstruction[] instructions = program.Instructions.Select(i => i.Index == shift.Index ? replacement : i).ToArray();
        IrProgram raw = program with { Instructions = instructions,
            ControlFlowGraph = new([program.BasicBlocks[0] with { Instructions = instructions }], program.ControlFlowGraph.Edges),
            ValueFlow = program.ValueFlow with { Accesses = program.ValueFlow.Accesses
                .Where(a => a.InstructionIndex != shift.Index || a.Kind == IrValueAccessKind.Def).ToArray() } };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(raw);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
            new HybridCPU.Compiler.Core.IR.Resources.HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
            resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Unsupported, allocation.Status);
        Assert.Null(allocation.Witness);
        Assert.Same(schedule, allocation.FinalSchedule); Assert.Same(bundles, allocation.FinalBundles);
        Assert.True(HybridCpuConstantMaterializationPlanV1.RequiresRegisterConstantMaterialization(opcode, 0));
        Assert.False(HybridCpuConstantMaterializationPlanV1.RequiresRegisterConstantMaterialization(opcode, 1));
        Assert.False(HybridCpuConstantMaterializationPlanV1.RequiresRegisterConstants(opcode));
    }

    [Fact]
    public void LlvmXorConstants_ProduceWitnessAfterExplicitMaterialization()
    {
        if (!PinnedRuntimeAvailable()) return;
        LlvmSemanticMappingResultV1 mapped = MapText(SupportedModuleText.Replace("add nsw i32 1, 2", "xor i32 5, 3", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", "xor i32 %sum, 1", StringComparison.Ordinal));
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(Assert.IsType<IrProgram>(mapped.Program));
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
            new HybridCPU.Compiler.Core.IR.Resources.HybridCpuMachineTopologyV1(64,64,4,8,2,16),8);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule,bundles,
            resourceModel:resources,options:HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated,allocation.Status);
        Assert.NotNull(allocation.Witness);
        Assert.DoesNotContain(schedule.Program.Instructions.Where(i => i.Opcode == HybridCpuOpcode.XOR),
            i => i.Annotation.Uses.Any(o => o.Kind == IrOperandKind.Constant));
        Assert.NotEmpty(HybridCpuIrProgramCompiler.CompileAllocated(allocation).ProgramImage);
    }
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void RealLlvmXor_MapsIntegerSsaAndReachesCanonicalScheduler(int width)
    {
        if (!PinnedRuntimeAvailable()) return;
        string text = SupportedModuleText.Replace("add nsw i32 1, 2", $"xor i{width} 5, 3", StringComparison.Ordinal)
            .Replace("mul i32 %sum, 4", $"xor i{width} %sum, 1", StringComparison.Ordinal);
        LlvmSemanticMappingResultV1 result = MapText(text);
        Assert.Equal(LlvmSemanticMappingStatusV1.Success, result.Status);
        IrProgram program = Assert.IsType<IrProgram>(result.Program);
        IrInstruction[] xors = program.Instructions.Where(i => i.Opcode == HybridCpuOpcode.XOR).ToArray();
        Assert.Equal(2, xors.Length);
        Assert.All(xors, i => Assert.Equal(width, i.CanonicalType.BitWidth));
        Assert.All(xors, i => Assert.Equal(IrIntegerOverflowSemantics.Wrap, i.Semantics.IntegerOverflow));
        Assert.Equal(5, program.ValueFlow.Values.Count);
        Assert.Single(new HybridCpuLocalListScheduler().ScheduleProgram(program).BlockSchedules);
    }
    [Fact]
    public void MappingContract_IsCompleteDeterministicAndNonAuthoritative()
    {
        LlvmSemanticMappingContractV1 contract = LlvmSemanticMappingContractV1.Default;
        Assert.Equal(37, contract.Entries.Count);
        Assert.Equal(37, contract.Entries.Select(static row => row.SourceIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("5b8a6c1bf9d9510b8d4cfdbbcde72e010fbf02173816d7066699231b57dcc29a", contract.ContractDigest);
        Assert.False(contract.HasTargetLegalityAuthority);
        Assert.False(contract.HasRuntimeAuthority);
        Assert.Equal(LlvmToolchainContractV1.Default.ContractDigest, contract.ToolchainContractDigest);
        Assert.Equal(LlvmToolchainContractV1.Default.CoreTargetContractDigest, contract.CoreTargetContractDigest);
        Assert.Equal(LlvmToolchainContractV1.Default.PlatformContractDigest, contract.PlatformContractDigest);
        Assert.Contains(contract.Entries, static row => row.SourceIdentity == "value.poison" && row.Support == LlvmSemanticMappingSupportV1.Unsupported);
        Assert.Contains(contract.Entries, static row => row.SourceIdentity == "memory.volatile" && row.Support == LlvmSemanticMappingSupportV1.Unsupported);
        Assert.Contains(contract.Entries, static row => row.SourceIdentity == "analysis.profile.noalias" && row.FailureCode == "HCLM0028");
    }

    [Fact]
    public void RealLlvmIntegerPrimitive_MapsLosslesslyToCanonicalIrAndValueFlow()
    {
        if (!PinnedRuntimeAvailable()) return;
        LlvmSemanticMappingResultV1 result = MapText(SupportedModuleText);

        Assert.Equal(LlvmSemanticMappingStatusV1.Success, result.Status);
        IrProgram program = Assert.IsType<IrProgram>(result.Program);
        Assert.Equal(6, program.Instructions.Count);
        Assert.Equal(HybridCpuOpcode.ADD, program.Instructions[2].Opcode);
        Assert.Equal(HybridCpuOpcode.MUL, program.Instructions[4].Opcode);
        Assert.Equal(HybridCpuOpcode.JALR, program.Instructions[5].Opcode);
        Assert.Equal(IrIntegerOverflowSemantics.SourcePoison, program.Instructions[2].Semantics.IntegerOverflow);
        Assert.Equal(IrUndefinedValueSemantics.SourcePoison, program.Instructions[2].Semantics.UndefinedValue);
        Assert.Equal(IrShiftSemantics.NotApplicable, program.Instructions[2].Semantics.Shift);
        Assert.Equal(IrIntegerOverflowSemantics.NotApplicable, program.Instructions[5].Semantics.IntegerOverflow);
        Assert.Equal(IrArchitecturalEffectKind.Control | IrArchitecturalEffectKind.Return,
            program.Instructions[5].SideEffects.ArchitecturalEffects);
        Assert.Equal(5, program.ValueFlow.Values.Count);
        Assert.All(program.ValueFlow.Values, static value =>
        {
            Assert.True(value.ValueKind.BitWidth is 32 or 64);
            Assert.True(value.Allocation.IsAllocatable);
        });
        Assert.Equal(5, program.ValueFlow.Accesses.Count(static access => access.Kind == IrValueAccessKind.Def));
        Assert.Equal(4, program.ValueFlow.Accesses.Count(static access => access.Kind == IrValueAccessKind.Use));
        Assert.Equal(IrValueAnalysisStatus.Complete,
            new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program).Status);
        IrBasicBlockDependencyGraph dependencies =
            new HybridCpuBasicBlockDependencyAnalyzer().AnalyzeBlock(program.BasicBlocks[0]);
        Assert.Contains(dependencies.Dependencies, static dependency =>
            dependency.Kind == IrInstructionDependencyKind.RegisterRaw &&
            dependency.ProducerInstructionIndex == 2 && dependency.ConsumerInstructionIndex == 4 &&
            dependency.RelatedOperandKind == IrOperandKind.VirtualValue);
        Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(program).Status);
        Assert.All(program.Instructions, static instruction =>
        {
            Assert.Equal(IrSourceOriginKind.Llvm, Assert.Single(instruction.OriginChain.Links).Kind);
            Assert.DoesNotContain("LLVMValueRef", instruction.GetType().FullName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SemanticMapping_IsDeterministicAndTextBitcodeHaveCanonicalParity()
    {
        if (!PinnedRuntimeAvailable()) return;
        string textPath = CreateTempPath("ll");
        string bitcodePath = CreateTempPath("bc");
        try
        {
            File.WriteAllText(textPath, SupportedModuleText, new UTF8Encoding(false));
            AssembleBitcode(textPath, bitcodePath);
            LlvmSemanticMapperV1 mapper = new LlvmSemanticMapperV1();
            LlvmSemanticMappingResultV1 first = mapper.MapFile(textPath, LlvmInputKind.TextIr);
            LlvmSemanticMappingResultV1 second = mapper.MapFile(textPath, LlvmInputKind.TextIr);
            LlvmSemanticMappingResultV1 bitcode = mapper.MapFile(bitcodePath, LlvmInputKind.Bitcode);
            IrProgram firstProgram = Assert.IsType<IrProgram>(first.Program);
            IrProgram secondProgram = Assert.IsType<IrProgram>(second.Program);
            IrProgram bitcodeProgram = Assert.IsType<IrProgram>(bitcode.Program);

            Assert.Equal(first.Status, second.Status);
            Assert.Equal(Project(firstProgram), Project(secondProgram));
            Assert.Equal(firstProgram.FrontendEvidence.Digest, secondProgram.FrontendEvidence.Digest);
            Assert.Equal(first.Diagnostics, second.Diagnostics);
            Assert.Equal(LlvmSemanticMappingStatusV1.Success, bitcode.Status);
            Assert.Equal(Project(firstProgram), Project(bitcodeProgram));
            Assert.NotEqual(first.ImportProvenance!.InputDigest, bitcode.ImportProvenance!.InputDigest);
        }
        finally
        {
            File.Delete(textPath);
            File.Delete(bitcodePath);
        }
    }

    [Fact]
    public void Phase09DefaultImport_RemainsStructuralAndRejectsSemanticOpcode()
    {
        string path = CreateTempPath("ll");
        try
        {
            File.WriteAllText(path, SupportedModuleText, new UTF8Encoding(false));
            LlvmImportResultV1 result = new LlvmModuleImporterV1().ImportFile(path, LlvmInputKind.TextIr);
            if (!PinnedRuntimeAvailable())
            {
                Assert.Equal(LlvmImportStatus.Unavailable, result.Status);
                Assert.Equal("HCLL0002", Assert.Single(result.Diagnostics).Code);
                return;
            }
            Assert.Equal(LlvmImportStatus.Unsupported, result.Status);
            Assert.Equal("HCLL0014", Assert.Single(result.Diagnostics).Code);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("add i32 1, 2", IrIntegerOverflowSemantics.Wrap)]
    [InlineData("add nsw i32 1, 2", IrIntegerOverflowSemantics.SourcePoison)]
    [InlineData("add nuw i32 1, 2", IrIntegerOverflowSemantics.SourcePoison)]
    public void IntegerOverflowFlags_AreMappedWithoutWeakening(
        string operation,
        IrIntegerOverflowSemantics expected)
    {
        if (!PinnedRuntimeAvailable()) return;
        string module = SupportedModuleText.Replace("add nsw i32 1, 2", operation, StringComparison.Ordinal);
        IrProgram program = Assert.IsType<IrProgram>(MapText(module).Program);
        Assert.Equal(expected, program.Instructions[2].Semantics.IntegerOverflow);
        Assert.Equal(expected == IrIntegerOverflowSemantics.SourcePoison
            ? IrUndefinedValueSemantics.SourcePoison
            : IrUndefinedValueSemantics.NotApplicable,
            program.Instructions[2].Semantics.UndefinedValue);
    }

    [Theory]
    [MemberData(nameof(RejectedSemanticModules))]
    public void UnsupportedOrWeakenedSemantics_FailClosedDeterministically(string module, string code)
    {
        if (!PinnedRuntimeAvailable())
        {
            LlvmSemanticMappingResultV1 unavailable = MapText(module);
            Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, unavailable.Status);
            Assert.Null(unavailable.Program);
            Assert.Equal("HCLL0002", Assert.Single(unavailable.Diagnostics).Code);
            return;
        }
        LlvmSemanticMappingResultV1 first = MapText(module);
        LlvmSemanticMappingResultV1 second = MapText(module);
        Assert.NotEqual(LlvmSemanticMappingStatusV1.Success, first.Status);
        Assert.Null(first.Program);
        Assert.Equal(code, Assert.Single(first.Diagnostics).Code);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(
            first.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Message)),
            second.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Message)));
    }

    [Fact]
    public void ProfileAndTbaaCannotStrengthenAliasFactsOrLegality()
    {
        if (!PinnedRuntimeAvailable()) return;
        IrProgram program = Assert.IsType<IrProgram>(MapText(SupportedModuleText).Program);
        IrFrontendAnalysisFactV1 profile = Fact(IrAnalysisEvidenceKind.Profile,
            IrAnalysisEvidenceTrust.ProfileOnly, IrAliasEvidencePrecision.NoAlias, legality: true);
        IrFrontendAdapterResultV1 profileResult = CanonicalIrFrontendBoundaryV1.Validate(
            program with { FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Create([profile]) });
        Assert.Equal("HCIR0011", Assert.Single(profileResult.Diagnostics).Code);

        IrFrontendAnalysisFactV1 tbaa = Fact(IrAnalysisEvidenceKind.Tbaa,
            IrAnalysisEvidenceTrust.FrontendStaticEvidence, IrAliasEvidencePrecision.NoAlias, legality: false);
        IrFrontendAdapterResultV1 tbaaResult = CanonicalIrFrontendBoundaryV1.Validate(
            program with { FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Create([tbaa]) });
        Assert.Equal("HCIR0012", Assert.Single(tbaaResult.Diagnostics).Code);

        IrFrontendAnalysisEvidenceSetV1 valid = IrFrontendAnalysisEvidenceSetV1.Create([
            Fact(IrAnalysisEvidenceKind.Profile, IrAnalysisEvidenceTrust.ProfileOnly,
                IrAliasEvidencePrecision.MayAlias, legality: false)
        ]);
        IrFrontendAdapterResultV1 tampered = CanonicalIrFrontendBoundaryV1.Validate(
            program with { FrontendEvidence = valid with { Digest = new string('0', 64) } });
        Assert.Equal("HCIR0009", Assert.Single(tampered.Diagnostics).Code);
    }

    [Fact]
    public void OptionalFrontendFacts_CanBeStrippedWithoutChangingLegalityOrCanonicalOperations()
    {
        if (!PinnedRuntimeAvailable()) return;
        IrProgram mapped = Assert.IsType<IrProgram>(MapText(SupportedModuleText).Program);
        IrProgram stripped = mapped with { FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Empty };
        Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(stripped).Status);
        Assert.Equal(mapped.Instructions, stripped.Instructions);
        Assert.Equal(mapped.ValueFlow, stripped.ValueFlow);
    }

    public static TheoryData<string, string> RejectedSemanticModules => new()
    {
        { SupportedModuleText.Replace("add nsw i32 1, 2", "sdiv i32 4, 2", StringComparison.Ordinal), "HCLL0014" },
        { SupportedModuleText.Replace("add nsw i32 1, 2", "add i32 undef, 2", StringComparison.Ordinal), "HCLM0004" },
        { SupportedModuleText.Replace("%sum = add nsw i32 1, 2\n  %product = mul i32 %sum, 4", "%sum = add i24 1, 2\n  %product = mul i24 %sum, 4", StringComparison.Ordinal), "HCLL0026" },
        { SupportedModuleText.Replace("ret void", "br label %next\nnext:\n  ret void", StringComparison.Ordinal), "HCLM0026" },
        { SupportedModuleText.Replace("e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64", "e-p:32:32", StringComparison.Ordinal), "HCLL0012" },
        { SupportedModuleText.Replace("add nsw i32 1, 2", "load volatile i32, ptr null, align 4", StringComparison.Ordinal), "HCLL0014" },
        { SupportedModuleText + "\n!tbaa = !{!0}\n", "HCLL0018" }
    };

    private static IrFrontendAnalysisFactV1 Fact(
        IrAnalysisEvidenceKind kind,
        IrAnalysisEvidenceTrust trust,
        IrAliasEvidencePrecision precision,
        bool legality) =>
        new("fact:1", kind, trust, precision, "test", "1", new string('a', 64), legality, true);

    private static LlvmSemanticMappingResultV1 MapText(string text)
    {
        string path = CreateTempPath("ll");
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return new LlvmSemanticMapperV1().MapFile(path, LlvmInputKind.TextIr);
        }
        finally { File.Delete(path); }
    }

    private static bool PinnedRuntimeAvailable() => new LlvmModuleImporterV1().ProbeRuntime().IsAvailable;

    private const string SupportedModuleText = """
        target datalayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64"
        target triple = "hybridcpuv2-unknown-none"

        define void @kernel() {
        entry:
          %sum = add nsw i32 1, 2
          %product = mul i32 %sum, 4
          ret void
        }

        !llvm.ident = !{!0}
        !0 = !{!"LLVM 20.1.2"}
        """;

    private static string CreateTempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"hybridcpu-phase10-{Guid.NewGuid():N}.{extension}");

    private static string Project(IrProgram program) => string.Join('|',
        program.Instructions.Select(instruction => string.Join(':', instruction.Index, instruction.StableIdentity,
            instruction.Opcode, instruction.CanonicalType.Kind, instruction.CanonicalType.BitWidth,
            instruction.Semantics.IntegerOverflow, instruction.Semantics.UndefinedValue,
            instruction.SideEffects.Memory.Kind, instruction.SideEffects.ArchitecturalEffects,
            string.Join(',', instruction.Operands.Select(static operand => $"{operand.Kind}/{operand.Value}/{operand.Name}"))))
        .Concat(program.ValueFlow.Values.Select(value => $"v:{value.StableId}:{value.ValueKind.Kind}:{value.ValueKind.BitWidth}"))
        .Concat(program.ValueFlow.Accesses.Select(access => $"a:{access.ValueId}:{access.InstructionIndex}:{access.Kind}")));

    private static unsafe void AssembleBitcode(string textPath, string bitcodePath)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(textPath) + '\0');
        LLVMContextRef context = default;
        LLVMMemoryBufferRef buffer = default;
        LLVMModuleRef module = default;
        sbyte* message = null;
        try
        {
            fixed (byte* pathPointer = pathBytes)
            {
                LLVMOpaqueMemoryBuffer* rawBuffer = null;
                Assert.Equal(0, LLVM.CreateMemoryBufferWithContentsOfFile((sbyte*)pathPointer, &rawBuffer, &message));
                buffer = rawBuffer;
            }
            context = LLVMContextRef.Create();
            bool parsed = context.TryParseIR(buffer, out module, out string parseMessage);
            buffer = default;
            Assert.True(parsed, parseMessage);
            Assert.Equal(0, module.WriteBitcodeToFile(bitcodePath));
        }
        finally
        {
            if (message != null) LLVM.DisposeMessage(message);
            if (module != default) module.Dispose();
            if (context != default) context.Dispose();
            if (buffer != default) LLVM.DisposeMemoryBuffer(buffer);
        }
    }
}
