using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase21RestrictedCilFrontendTests
{
    private static readonly string FixtureAssembly = typeof(RestrictedCilCSharpFixtures).Assembly.Location;

    [Fact]
    public void SupportMatrix_IsClosedDeterministicAndNonAuthoritative()
    {
        RestrictedCilSupportMatrixV1 first = RestrictedCilSupportMatrixV1.Default;
        RestrictedCilSupportMatrixV1 second = RestrictedCilSupportMatrixV1.Default;

        Assert.Equal("hybridcpu.restricted-cil-matrix/v1", first.SchemaId);
        Assert.Equal(71, first.Opcodes.Count);
        Assert.Equal(23, first.Features.Count);
        Assert.Equal(20, first.Helpers.Count);
        Assert.Equal(first.ContractDigest, second.ContractDigest);
        Assert.Equal(64, first.ContractDigest.Length);
        Assert.False(first.HasRuntimeAuthority);
        Assert.False(first.HasTargetLegalityAuthority);
        Assert.False(first.AllowsHostFallback);
        Assert.Single(first.Opcodes, static row => row.Support == RestrictedCilMatrixSupportV1.Unsupported && row.Encoding == 0x8f);
        Assert.All(first.Opcodes.Where(static row => row.Encoding != 0x8f), static row =>
            Assert.Equal(RestrictedCilMatrixSupportV1.Supported, row.Support));
        Assert.Contains(first.Features, static row => row.Feature == "managed-references" &&
            row.Support == RestrictedCilMatrixSupportV1.Supported);
        Assert.Contains(first.Features, static row => row.Feature == "runtime-fallback" && row.Support == RestrictedCilMatrixSupportV1.Unsupported);
        Assert.Equal("canonical-inline/no-runtime-call", first.Helpers.Single(static helper =>
            helper.StableIdentity.Contains("Int32Helpers.Identity", StringComparison.Ordinal)).CallingConvention);
        Assert.Equal("intrinsic-noop", first.Helpers.Single(static helper =>
            helper.StableIdentity == "System.Object..ctor(System.Object):System.Void").CanonicalExpansion);
    }

    [Fact]
    public void RealCSharpArithmetic_ImportsToFrontendNeutralCanonicalIr()
    {
        RestrictedCilImportResultV1 result = Import(nameof(RestrictedCilCSharpFixtures.Add));

        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            $"{result.Status}: {string.Join(" | ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}"))}");
        IrProgram program = Assert.IsType<IrProgram>(result.Program);
        Assert.Equal([HybridCpuOpcode.ADD, HybridCpuOpcode.JALR], program.Instructions.Select(static instruction => instruction.Opcode));
        Assert.Equal(3, program.ValueFlow.Values.Count);
        Assert.Equal(
            HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Take(2).Select(static register => (int?)register),
            program.ValueFlow.Values.Where(static value => value.StableId.Contains(":arg:", StringComparison.Ordinal))
                .OrderBy(static value => value.StableId, StringComparer.Ordinal)
                .Select(static value => value.Allocation.FixedRegisterId));
        Assert.Equal(3, program.ValueFlow.Accesses.Count(static access => access.Kind == IrValueAccessKind.Use));
        Assert.Equal(1, program.ValueFlow.Accesses.Count(static access => access.Kind == IrValueAccessKind.Def));
        Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(program).Status);
        Assert.Matches("^0x06[0-9a-f]{6}$", result.Provenance!.CilMethodToken);
        Assert.All(program.Instructions, static instruction =>
        {
            Assert.Equal(IrSourceOriginKind.Cil, Assert.Single(instruction.OriginChain.Links).Kind);
            Assert.DoesNotContain("token", instruction.StableIdentity, StringComparison.OrdinalIgnoreCase);
            Assert.True(instruction.Semantics.IsFullySpecified);
        });
    }

    [Fact]
    public void RealCSharpPureHelperCall_UsesExactAllowlistedExpansion()
    {
        RestrictedCilImportResultV1 result = Import(nameof(RestrictedCilCSharpFixtures.CallIdentity));

        Assert.Equal(RestrictedCilImportStatusV1.Success, result.Status);
        IrProgram program = Assert.IsType<IrProgram>(result.Program);
        Assert.Equal([HybridCpuOpcode.ADDI, HybridCpuOpcode.JALR], program.Instructions.Select(static instruction => instruction.Opcode));
        Assert.All(program.Instructions, static instruction => Assert.Equal(IrMemoryEffectKind.None, instruction.SideEffects.Memory.Kind));
        Assert.DoesNotContain(program.Instructions, static instruction => instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call));
    }

    [Fact]
    public void RealCSharpControlFlow_MapsBranchesAndResolvedCanonicalCfg()
    {
        RestrictedCilImportResultV1 result = Import(nameof(RestrictedCilCSharpFixtures.EarlyReturn));

        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            $"{result.Status}: {string.Join(" | ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}"))}");
        IrProgram program = Assert.IsType<IrProgram>(result.Program);
        Assert.Contains(program.Instructions, static instruction => instruction.Opcode is HybridCpuOpcode.BEQ or HybridCpuOpcode.BNE);
        Assert.All(program.Instructions.Where(static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.ConditionalBranch),
            static instruction => Assert.NotNull(instruction.Annotation.ResolvedBranchTargetInstructionIndex));
        Assert.True(program.BasicBlocks.Count >= 2);
        Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(program).Status);
    }

    [Fact]
    public void ArithmeticCanonicalIr_HasFunctionalParityWithHandBuiltExpression()
    {
        IrProgram program = Assert.IsType<IrProgram>(Import(nameof(RestrictedCilCSharpFixtures.Add)).Program);

        Assert.Equal(19, Evaluate(program, 7, 12));
        Assert.Equal(unchecked((int)0x80000000), Evaluate(program, int.MaxValue, 1));
        Assert.Equal(IrIntegerOverflowSemantics.Wrap, program.Instructions[0].Semantics.IntegerOverflow);
    }

    [Fact]
    public void ImportedProgram_SchedulesBundlesLowersAndSerializesDeterministically()
    {
        IrProgram firstProgram = Assert.IsType<IrProgram>(Import(nameof(RestrictedCilCSharpFixtures.Add)).Program);
        IrProgram secondProgram = Assert.IsType<IrProgram>(Import(nameof(RestrictedCilCSharpFixtures.Add)).Program);

        byte[] first = Compile(firstProgram);
        byte[] second = Compile(secondProgram);
        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Equal(Project(firstProgram), Project(secondProgram));
    }

    [Fact]
    public void Import_IsDeterministicAcrossDiagnosticsProvenanceAndOptions()
    {
        RestrictedCilImportResultV1 first = Import(nameof(RestrictedCilCSharpFixtures.Add));
        RestrictedCilImportResultV1 second = Import(nameof(RestrictedCilCSharpFixtures.Add));

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(first.Provenance, second.Provenance);
        Assert.Equal(Project(Assert.IsType<IrProgram>(first.Program)), Project(Assert.IsType<IrProgram>(second.Program)));
    }

    [Theory]
    [InlineData(nameof(RestrictedCilCSharpFixtures.StringLength), RestrictedCilImportStatusV1.Unsupported, "HCCIL1001")]
    [InlineData(nameof(RestrictedCilCSharpFixtures.GenericIdentity), RestrictedCilImportStatusV1.Unsupported, "HCCIL1012")]
    [InlineData(nameof(RestrictedCilCSharpFixtures.TryCatch), RestrictedCilImportStatusV1.Unsupported, "HCCIL1011")]
    [InlineData(nameof(RestrictedCilCSharpFixtures.CallUnknown), RestrictedCilImportStatusV1.Unsupported, "HCCIL1474")]
    public void UnsupportedManagedFeatures_FailClosedDeterministically(
        string method,
        RestrictedCilImportStatusV1 status,
        string code)
    {
        RestrictedCilImportResultV1 first = Import(method);
        RestrictedCilImportResultV1 second = Import(method);

        Assert.Equal(status, first.Status);
        Assert.Null(first.Program);
        Assert.Equal(code, Assert.Single(first.Diagnostics).Code);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public void InstanceMethod_IsUnsupportedRatherThanExecutedByHost()
    {
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1().ImportFile(FixtureAssembly,
            new(typeof(RestrictedCilInstanceFixture).FullName!, nameof(RestrictedCilInstanceFixture.Increment)));

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Equal("HCCIL1004", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Program);
    }

    [Fact]
    public void DeclaringTypeWithStaticConstructor_IsRejectedBeforeBodyImport()
    {
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1().ImportFile(FixtureAssembly,
            new(typeof(RestrictedCilStaticInitializationFixture).FullName!, nameof(RestrictedCilStaticInitializationFixture.Read)));

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Equal("HCCIL1016", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void PInvokeMethod_IsRejectedBeforeAnyInteropTransition()
    {
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1().ImportFile(FixtureAssembly,
            new(typeof(RestrictedCilInteropFixture).FullName!, nameof(RestrictedCilInteropFixture.GetCurrentProcessId)));

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Equal("HCCIL1015", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Program);
    }

    [Fact]
    public void InvalidPeAndMissingSelector_AreInvalidNotUnsupported()
    {
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1();
        RestrictedCilImportResultV1 invalid = importer.ImportImage(new byte[] { 1, 2, 3, 4 },
            new(typeof(RestrictedCilCSharpFixtures).FullName!, nameof(RestrictedCilCSharpFixtures.Add)));
        RestrictedCilImportResultV1 missing = importer.ImportFile(FixtureAssembly,
            new(typeof(RestrictedCilCSharpFixtures).FullName!, "Absent"));

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, invalid.Status);
        Assert.Equal("HCCIL0005", Assert.Single(invalid.Diagnostics).Code);
        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, missing.Status);
        Assert.Equal("HCCIL0008", Assert.Single(missing.Diagnostics).Code);
    }

    [Fact]
    public void MalformedCilOperand_IsInvalidRatherThanUnsupported()
    {
        byte[] image = MutateMethodBody(nameof(RestrictedCilCSharpFixtures.Add), static body => body[^1] = 0x20);
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1().ImportImage(image,
            new(typeof(RestrictedCilCSharpFixtures).FullName!, nameof(RestrictedCilCSharpFixtures.Add)), "malformed-add.dll");

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, result.Status);
        Assert.Equal("HCCIL0028", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void InvalidTypeStack_IsDistinctFromValidUnsupportedOpcode()
    {
        byte[] invalidImage = MutateMethodBody(nameof(RestrictedCilCSharpFixtures.Add), static body => body[0] = 0x58);
        byte[] unsupportedImage = MutateMethodBody(nameof(RestrictedCilCSharpFixtures.CallIdentity), static body => body[0] = 0x8f);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(RestrictedCilCSharpFixtures).FullName!, nameof(RestrictedCilCSharpFixtures.Add));
        RestrictedCilMethodSelectorV1 unsupportedSelector = new RestrictedCilMethodSelectorV1(typeof(RestrictedCilCSharpFixtures).FullName!, nameof(RestrictedCilCSharpFixtures.CallIdentity));
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1();

        RestrictedCilImportResultV1 invalid = importer.ImportImage(invalidImage, selector, "invalid-stack.dll");
        RestrictedCilImportResultV1 unsupported = importer.ImportImage(unsupportedImage, unsupportedSelector, "unsupported-opcode.dll");

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, invalid.Status);
        Assert.Equal("HCCIL0012", Assert.Single(invalid.Diagnostics).Code);
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, unsupported.Status);
        Assert.Equal("HCCIL1408", Assert.Single(unsupported.Diagnostics).Code);
    }

    [Fact]
    public void DeterministicPeBudget_FailsWithoutReadingOrExecutingCil()
    {
        RestrictedCilImportBudgetsV1 budgets = RestrictedCilImportBudgetsV1.Production with { MaximumPeBytes = 32 };
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1(budgets).ImportFile(FixtureAssembly,
            new(typeof(RestrictedCilCSharpFixtures).FullName!, nameof(RestrictedCilCSharpFixtures.Add)));

        Assert.Equal(RestrictedCilImportStatusV1.BudgetExhausted, result.Status);
        Assert.Equal("HCCIL2001", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Program);
    }

    [Fact]
    public void InvalidBudgets_FailBeforePeProcessing()
    {
        RestrictedCilImportBudgetsV1 budgets = RestrictedCilImportBudgetsV1.Production with { MaximumDecodedInstructions = 0 };
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1(budgets).ImportImage(new byte[] { 1 },
            new("x", "y"));

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, result.Status);
        Assert.Equal("HCCIL0002", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CilProject_DependsOnlyInwardOnCoreAndHasNoFrontendPackages()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Cil", "HybridCPU.Compiler.Cil.csproj"));
        string coreProject = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Core", "HybridCPU.Compiler.Core.csproj"));

        Assert.Contains("..\\Core\\HybridCPU.Compiler.Core.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Roslyn", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LLVMSharp", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cil", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Reflection.Metadata", coreProject, StringComparison.Ordinal);
    }

    [Fact]
    public void ImporterSurface_HasNoExecutionFallbackOrFrontendHandleInCore()
    {
        Type resultType = typeof(RestrictedCilImportResultV1);
        Assert.All(resultType.GetProperties(), static property =>
        {
            string name = property.PropertyType.FullName ?? property.PropertyType.Name;
            Assert.DoesNotContain("MetadataToken", name, StringComparison.Ordinal);
            Assert.DoesNotContain("MethodDefinitionHandle", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Roslyn", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LLVM", name, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(typeof(IrInstruction).Assembly.GetReferencedAssemblies(),
            static reference => reference.Name is "System.Reflection.Metadata" or "Microsoft.CodeAnalysis" or "LLVMSharp.Interop");
    }

    private static RestrictedCilImportResultV1 Import(string method) =>
        new RestrictedCilImporterV1().ImportFile(FixtureAssembly,
            new(typeof(RestrictedCilCSharpFixtures).FullName!, method));

    private static byte[] Compile(IrProgram program)
    {
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IReadOnlyList<HybridCpuInstructionBundle> lowered = new HybridCpuBundleLowerer().LowerProgram(bundles);
        return new HybridCpuBundleSerializer().SerializeProgram(lowered);
    }

    private static int Evaluate(IrProgram program, int left, int right)
    {
        Dictionary<string, int> values = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [$"cil:{typeof(RestrictedCilCSharpFixtures).FullName}.{nameof(RestrictedCilCSharpFixtures.Add)}:arg:0"] = left,
            [$"cil:{typeof(RestrictedCilCSharpFixtures).FullName}.{nameof(RestrictedCilCSharpFixtures.Add)}:arg:1"] = right
        };
        foreach (IrInstruction instruction in program.Instructions)
        {
            int Read(IrOperand operand) => operand.Kind == IrOperandKind.Constant ? unchecked((int)operand.Value) : values[operand.Name];
            if (instruction.Opcode == HybridCpuOpcode.ADD)
                values[Assert.Single(instruction.Annotation.Defs).Name] = unchecked(Read(instruction.Annotation.Uses[0]) + Read(instruction.Annotation.Uses[1]));
            if (instruction.Opcode == HybridCpuOpcode.JALR)
                return Read(Assert.Single(instruction.Annotation.Uses));
        }
        throw new InvalidOperationException("No return operation was mapped.");
    }

    private static string Project(IrProgram program) => string.Join('|',
        program.Instructions.Select(instruction => string.Join(':', instruction.Index, instruction.StableIdentity,
            instruction.Opcode, instruction.CanonicalType.Kind, instruction.CanonicalType.BitWidth,
            instruction.Semantics.IntegerOverflow, instruction.SideEffects.ArchitecturalEffects,
            string.Join(',', instruction.Operands.Select(static operand => $"{operand.Kind}/{operand.Value}/{operand.Name}"))))
        .Concat(program.ValueFlow.Values.Select(value => $"v:{value.StableId}:{value.ValueKind.Kind}:{value.ValueKind.BitWidth}"))
        .Concat(program.ValueFlow.Accesses.Select(access => $"a:{access.ValueId}:{access.InstructionIndex}:{access.Kind}")));

    private static string FindRepositoryRoot()
    {
        return HybridCPU_ISE.Tests.TestHelpers.CompatFreezeScanner.FindCompilerRepoRoot();
    }

    private delegate void SpanMutation(Span<byte> body);

    private static byte[] MutateMethodBody(string methodName, SpanMutation mutation)
    {
        byte[] image = File.ReadAllBytes(FixtureAssembly);
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition method = metadata.MethodDefinitions
            .Select(metadata.GetMethodDefinition)
            .Single(candidate => string.Equals(metadata.GetString(candidate.Name), methodName, StringComparison.Ordinal));
        int rva = method.RelativeVirtualAddress;
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(candidate =>
            rva >= candidate.VirtualAddress && rva < candidate.VirtualAddress + Math.Max(candidate.VirtualSize, candidate.SizeOfRawData));
        int bodyOffset = checked(rva - section.VirtualAddress + section.PointerToRawData);
        byte first = image[bodyOffset];
        int headerSize;
        int codeSize;
        if ((first & 3) == 2)
        {
            headerSize = 1;
            codeSize = first >> 2;
        }
        else
        {
            ushort flagsAndSize = BitConverter.ToUInt16(image, bodyOffset);
            headerSize = (flagsAndSize >> 12) * 4;
            codeSize = BitConverter.ToInt32(image, bodyOffset + 4);
        }
        mutation(image.AsSpan(bodyOffset + headerSize, codeSize));
        return image;
    }
}

public static class RestrictedCilCSharpFixtures
{
    public static int Add(int left, int right) => left + right;
    public static int CallIdentity(int value) => HybridCPU.RestrictedRuntime.Int32Helpers.Identity(value);
    public static void EarlyReturn(int condition) { if (condition != 0) return; }
    public static int StringLength(string value) => value.Length;
    public static T GenericIdentity<T>(T value) => value;
    public static int TryCatch(int value) { try { return 10 / value; } catch (DivideByZeroException) { return 0; } }
    public static int CallUnknown(int value) => Math.Abs(value);
}

public sealed class RestrictedCilInstanceFixture
{
    public int Increment(int value) => value + 1;
}

public static class RestrictedCilStaticInitializationFixture
{
    private static readonly int Value;
    static RestrictedCilStaticInitializationFixture() => Value = 7;
    public static int Read() => Value;
}

public static partial class RestrictedCilInteropFixture
{
    [LibraryImport("kernel32")]
    public static partial uint GetCurrentProcessId();
}
