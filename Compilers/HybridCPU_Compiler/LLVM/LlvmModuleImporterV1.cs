using System.Security.Cryptography;
using LLVMSharp.Interop;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Llvm;

public sealed unsafe class LlvmModuleImporterV1
{
    private enum ImportPolicy : byte
    {
        Phase09Structural = 0,
        Phase10SemanticSnapshot = 1
    }

    private static readonly HashSet<LLVMOpcode> Phase09StructuralOpcodes =
    [
        LLVMOpcode.LLVMRet,
        LLVMOpcode.LLVMBr,
        LLVMOpcode.LLVMUnreachable
    ];

    private static readonly HashSet<LLVMOpcode> Phase10MappedOpcodes =
    [
        LLVMOpcode.LLVMRet,
        LLVMOpcode.LLVMBr,
        LLVMOpcode.LLVMAdd,
        LLVMOpcode.LLVMSub,
        LLVMOpcode.LLVMMul,
        LLVMOpcode.LLVMXor,
        LLVMOpcode.LLVMAnd,
        LLVMOpcode.LLVMOr,
        LLVMOpcode.LLVMShl
    ];

    public LlvmRuntimeAvailabilityV1 ProbeRuntime()
    {
        try
        {
            string nativeLibraryPath = Path.Combine(AppContext.BaseDirectory, "libLLVM.dll");
            if (!File.Exists(nativeLibraryPath))
                return new(false, null, null, null, null, "HCLL0002", "Pinned LLVM runtime is unavailable: native library is absent.");
            using FileStream nativeLibrary = File.OpenRead(nativeLibraryPath);
            string nativeLibrarySha256 = Convert.ToHexString(SHA256.HashData(nativeLibrary)).ToLowerInvariant();
            if (!string.Equals(nativeLibrarySha256, LlvmToolchainContractV1.NativeLibrarySha256, StringComparison.Ordinal))
            {
                return new(false, null, null, null, nativeLibrarySha256, "HCLL0003",
                    "LLVM native-library digest does not equal the pinned runtime digest.");
            }
            uint major = 0;
            uint minor = 0;
            uint patch = 0;
            LLVM.GetVersion(&major, &minor, &patch);
            if (major != LlvmToolchainContractV1.LlvmMajor ||
                minor != LlvmToolchainContractV1.LlvmMinor ||
                patch != LlvmToolchainContractV1.LlvmPatch)
            {
                return new(false, (int)major, (int)minor, (int)patch, nativeLibrarySha256, "HCLL0003",
                    $"LLVM runtime {major}.{minor}.{patch} does not equal pinned {LlvmToolchainContractV1.LlvmRelease}.");
            }
            return new(true, (int)major, (int)minor, (int)patch, nativeLibrarySha256, "HCLL1000",
                $"Pinned LLVM runtime {major}.{minor}.{patch} is available.");
        }
        catch (Exception exception) when (IsNativeAvailabilityFailure(exception) || exception is IOException or UnauthorizedAccessException)
        {
            return new(false, null, null, null, null, "HCLL0002",
                $"Pinned LLVM runtime is unavailable: {exception.GetType().Name}.");
        }
    }

    public LlvmImportResultV1 ImportFile(string path, LlvmInputKind kind) =>
        ImportFile(path, kind, ImportPolicy.Phase09Structural);

    internal LlvmImportResultV1 ImportFileForSemanticMapping(string path, LlvmInputKind kind) =>
        ImportFile(path, kind, ImportPolicy.Phase10SemanticSnapshot);

    private LlvmImportResultV1 ImportFile(string path, LlvmInputKind kind, ImportPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(path)) return Failure(LlvmImportStatus.InvalidInput, "HCLL0001", "LLVM input path is absent.");
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(LlvmImportStatus.InvalidInput, "HCLL0001", "LLVM input path is invalid.");
        }
        string requiredExtension = kind == LlvmInputKind.TextIr ? ".ll" : ".bc";
        if (!string.Equals(Path.GetExtension(fullPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
            return Failure(LlvmImportStatus.InvalidInput, "HCLL0004", $"Input kind {kind} requires a {requiredExtension} file.", fullPath);
        if (!File.Exists(fullPath))
            return Failure(LlvmImportStatus.InvalidInput, "HCLL0005", "LLVM input file does not exist.", fullPath);

        FileInfo info = new FileInfo(fullPath);
        if (info.Length <= 0)
            return Failure(LlvmImportStatus.InvalidInput, "HCLL0005", "LLVM input file is empty.", fullPath);
        if (info.Length > LlvmToolchainContractV1.Default.Limits.MaximumInputBytes)
            return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0017",
                $"LLVM input byte budget {LlvmToolchainContractV1.Default.Limits.MaximumInputBytes} was exceeded.", fullPath);

        LlvmRuntimeAvailabilityV1 availability = ProbeRuntime();
        if (!availability.IsAvailable)
            return Failure(LlvmImportStatus.Unavailable, availability.DiagnosticCode, availability.Detail, fullPath);

        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(fullPath + '\0');
        LLVMMemoryBufferRef buffer = default;
        LLVMContextRef context = default;
        LLVMModuleRef module = default;
        sbyte* message = null;
        try
        {
            fixed (byte* pathPointer = pathBytes)
            {
                LLVMOpaqueMemoryBuffer* rawBuffer = null;
                int readFailed = LLVM.CreateMemoryBufferWithContentsOfFile((sbyte*)pathPointer, &rawBuffer, &message);
                if (readFailed != 0)
                    return Failure(LlvmImportStatus.InvalidInput, "HCLL0006", TakeMessage(ref message, "LLVM could not read input."), fullPath);
                buffer = rawBuffer;
            }

            context = LLVMContextRef.Create();
            bool parsed = context.TryParseIR(buffer, out module, out string parseMessage);
            // LLVMParseIRInContext consumes the memory buffer on both success and failure.
            buffer = default;
            if (!parsed)
                return Failure(LlvmImportStatus.InvalidInput, "HCLL0007", StableNativeMessage(parseMessage, "LLVM rejected input."), fullPath);
            if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string verifyMessage))
                return Failure(LlvmImportStatus.InvalidInput, "HCLL0008", StableNativeMessage(verifyMessage, "LLVM module verification failed."), fullPath);

            LlvmImportResultV1? compatibilityFailure = ValidateCompatibility(module, fullPath);
            if (compatibilityFailure is not null) return compatibilityFailure;
            return Summarize(module, fullPath, availability, policy);
        }
        catch (Exception exception) when (IsNativeAvailabilityFailure(exception))
        {
            return Failure(LlvmImportStatus.Unavailable, "HCLL0002",
                $"Pinned LLVM runtime became unavailable: {exception.GetType().Name}.", fullPath);
        }
        finally
        {
            if (message != null) LLVM.DisposeMessage(message);
            if (module != default) module.Dispose();
            if (context != default) context.Dispose();
            if (buffer != default) LLVM.DisposeMemoryBuffer(buffer);
        }
    }

    public LlvmImportResultV1 ValidateHeader(LlvmModuleHeaderV1 header, string? stableSourceIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        LlvmImportResultV1? targetFailure = ValidateTargetLayout(
            header.TargetTriple, header.DataLayout, stableSourceIdentity);
        if (targetFailure is not null) return targetFailure;
        if (string.IsNullOrWhiteSpace(header.ModuleProducer))
            return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0020", "LLVM module producer is absent.", stableSourceIdentity);
        if (!IsPinnedProducer(header.ModuleProducer))
            return Failure(LlvmImportStatus.Unsupported, "HCLL0021", "LLVM module producer does not match pinned 20.1.2.", stableSourceIdentity);
        return new(LlvmImportStatus.Success, null, Array.Empty<LlvmImporterDiagnosticV1>());
    }

    private static LlvmImportResultV1? ValidateTargetLayout(
        string targetTriple,
        string dataLayout,
        string? stableSourceIdentity)
    {
        if (string.IsNullOrWhiteSpace(targetTriple))
            return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0009", "LLVM target triple is absent; host defaults are forbidden.", stableSourceIdentity);
        if (!string.Equals(targetTriple, HybridCpuTargetMachineContractV1.TargetTriple, StringComparison.Ordinal))
            return Failure(LlvmImportStatus.Unsupported, "HCLL0010", $"LLVM target triple '{targetTriple}' is not supported.", stableSourceIdentity);
        if (string.IsNullOrWhiteSpace(dataLayout))
            return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0011", "LLVM DataLayout is absent; host defaults are forbidden.", stableSourceIdentity);
        if (!string.Equals(dataLayout, LlvmToolchainContractV1.HybridCpuLlvmDataLayout, StringComparison.Ordinal))
            return Failure(LlvmImportStatus.Unsupported, "HCLL0012", "LLVM DataLayout is incompatible with the verified HybridCPU target contract.", stableSourceIdentity);
        return null;
    }

    private static LlvmImportResultV1? ValidateCompatibility(LLVMModuleRef module, string source)
    {
        return ValidateTargetLayout(module.Target, module.DataLayout, source);
    }

    private static LlvmImportResultV1 Summarize(
        LLVMModuleRef module,
        string source,
        LlvmRuntimeAvailabilityV1 availability,
        ImportPolicy policy)
    {
        LlvmImporterLimitsV1 limits = LlvmToolchainContractV1.Default.Limits;
        if (module.FirstGlobal != default)
            return Failure(LlvmImportStatus.Unsupported, "HCLL0022",
                "LLVM globals await an explicit HybridCPU object and address-space contract.", source);
        int functions = 0;
        int blocks = 0;
        int instructions = 0;
        HashSet<LLVMTypeRef> types = new HashSet<LLVMTypeRef>();
        HashSet<LLVMValueRef> constants = new HashSet<LLVMValueRef>();
        List<LlvmSemanticFunctionSnapshotV1> semanticFunctions = new List<LlvmSemanticFunctionSnapshotV1>();
        LlvmImportResultV1? metadataFailure = ValidateMetadataAndProducer(module, source, out string producer);
        if (metadataFailure is not null) return metadataFailure;
        for (LLVMValueRef function = module.FirstFunction; function != default; function = function.NextFunction)
        {
            List<LlvmSemanticBasicBlockSnapshotV1> semanticBlocks = new List<LlvmSemanticBasicBlockSnapshotV1>();
            if (++functions > limits.MaximumFunctions)
                return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0013", "LLVM function budget was exceeded.", source);
            LLVMTypeRef functionType = LLVM.GlobalGetValueType(function);
            LlvmImportResultV1? functionTypeFailure = ValidateType(functionType, types, limits, source);
            if (functionTypeFailure is not null) return functionTypeFailure;
            if (function.ParamsCount != 0 ||
                functionType.ReturnType.Kind != LLVMTypeKind.LLVMVoidTypeKind ||
                function.FunctionCallConv != 0 ||
                (function.IsDeclaration && function.IntrinsicID == 0) ||
                (!function.IsDeclaration &&
                 function.GetAttributeCountAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex) != 0))
            {
                return Failure(LlvmImportStatus.Unsupported, "HCLL0023",
                    "LLVM function ABI or attributes are outside the verified HybridCPU primitive boundary.", source);
            }
            for (LLVMBasicBlockRef block = function.FirstBasicBlock; block != default; block = block.Next)
            {
                int blockId = semanticBlocks.Count;
                List<LlvmSemanticInstructionSnapshotV1> semanticInstructions = new List<LlvmSemanticInstructionSnapshotV1>();
                if (++blocks > limits.MaximumBasicBlocks)
                    return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0013", "LLVM basic-block budget was exceeded.", source);
                for (LLVMValueRef instruction = block.FirstInstruction; instruction != default; instruction = instruction.NextInstruction)
                {
                    if (++instructions > limits.MaximumInstructions)
                        return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0013", "LLVM instruction budget was exceeded.", source);
                    if (instruction.OperandCount > limits.MaximumOperandsPerInstruction)
                        return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0015", "LLVM operand budget was exceeded.", source);
                    LlvmImportResultV1? instructionTypeFailure = ValidateType(instruction.TypeOf, types, limits, source);
                    if (instructionTypeFailure is not null) return instructionTypeFailure;
                    for (uint operandIndex = 0; operandIndex < instruction.OperandCount; operandIndex++)
                    {
                        LLVMValueRef operand = instruction.GetOperand(operandIndex);
                        LlvmImportResultV1? operandTypeFailure = ValidateType(operand.TypeOf, types, limits, source);
                        if (operandTypeFailure is not null) return operandTypeFailure;
                        if (operand.IsConstant && constants.Add(operand) && constants.Count > limits.MaximumConstants)
                            return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0027", "LLVM constant budget was exceeded.", source);
                    }
                    if (instruction.InstructionOpcode == LLVMOpcode.LLVMCall)
                    {
                        LLVMValueRef called = LLVM.GetCalledValue(instruction);
                        if (called != default && called.IntrinsicID != 0)
                            return Failure(LlvmImportStatus.Unsupported, "HCLL0016", "LLVM intrinsic is not in the supported importer matrix.", source);
                    }
                    HashSet<LLVMOpcode> approvedOpcodes = policy == ImportPolicy.Phase09Structural
                        ? Phase09StructuralOpcodes
                        : Phase10MappedOpcodes;
                    if (!approvedOpcodes.Contains(instruction.InstructionOpcode))
                        return Failure(LlvmImportStatus.Unsupported, "HCLL0014",
                            $"LLVM opcode '{instruction.InstructionOpcode}' is outside the active semantic mapping firewall.", source);
                    if (policy == ImportPolicy.Phase10SemanticSnapshot)
                        semanticInstructions.Add(CreateSemanticInstructionSnapshot(instruction, instructions - 1, function.Name, blockId));
                }
                if (policy == ImportPolicy.Phase10SemanticSnapshot)
                    semanticBlocks.Add(new(blockId, $"llvm:{function.Name}:bb{blockId}", semanticInstructions));
            }
            if (policy == ImportPolicy.Phase10SemanticSnapshot)
                semanticFunctions.Add(new(function.Name, semanticBlocks));
        }

        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        LlvmToolchainContractV1 contract = LlvmToolchainContractV1.Default;
        LlvmImportProvenanceV1 provenance = new LlvmImportProvenanceV1(
            "hybridcpu.llvm-import-provenance/v1",
            LlvmToolchainContractV1.LlvmRelease,
            $"{availability.Major}.{availability.Minor}.{availability.Patch}",
            LlvmToolchainContractV1.LlvmSharpPackageVersion,
            LlvmToolchainContractV1.LlvmSharpRepositoryCommit,
            LlvmToolchainContractV1.NativeLibraryIdentity,
            availability.NativeLibrarySha256!,
            producer,
            digest,
            contract.OptionsDigest,
            contract.ContractDigest,
            contract.CoreTargetContractDigest,
            contract.PlatformContractDigest);
        return new(LlvmImportStatus.Success,
            new("hybridcpu.llvm-module-summary/v1", digest, producer, module.Target, module.DataLayout,
                functions, blocks, instructions, provenance,
                policy == ImportPolicy.Phase10SemanticSnapshot
                    ? new("hybridcpu.llvm-semantic-snapshot/v3", semanticFunctions)
                    : null),
            Array.Empty<LlvmImporterDiagnosticV1>());
    }

    private static LlvmSemanticInstructionSnapshotV1 CreateSemanticInstructionSnapshot(
        LLVMValueRef instruction,
        int index,
        string functionName,
        int blockId)
    {
        LLVMOpcode opcode = instruction.InstructionOpcode;
        List<LlvmSemanticOperandSnapshotV1> operands = new List<LlvmSemanticOperandSnapshotV1>(instruction.OperandCount);
        for (uint operandIndex = 0; operandIndex < instruction.OperandCount; operandIndex++)
        {
            LLVMValueRef operand = instruction.GetOperand(operandIndex);
            int bitWidth = operand.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind
                ? checked((int)operand.TypeOf.IntWidth)
                : 0;
            if (operand.IsAConstantInt != default)
                operands.Add(new(LlvmSnapshotOperandKindV1.ConstantInteger,
                    $"const:i{bitWidth}:{operand.ConstIntSExt}", bitWidth, operand.ConstIntSExt));
            else if (!string.IsNullOrWhiteSpace(operand.Name))
                operands.Add(new(LlvmSnapshotOperandKindV1.Value, $"llvm:{functionName}:value:{operand.Name}", bitWidth, null));
            else
                operands.Add(new(LlvmSnapshotOperandKindV1.Unknown,
                    $"llvm:{functionName}:bb{blockId}:i{index}:operand{operandIndex}", bitWidth, null));
        }

        bool arithmetic = opcode is LLVMOpcode.LLVMAdd or LLVMOpcode.LLVMSub or LLVMOpcode.LLVMMul or LLVMOpcode.LLVMShl;
        LLVMTypeRef type = instruction.TypeOf;
        return new LlvmSemanticInstructionSnapshotV1(
            index,
            $"llvm:{functionName}:bb{blockId}:i{index}",
            opcode.ToString(),
            instruction.Name,
            type.Kind.ToString(),
            type.Kind == LLVMTypeKind.LLVMIntegerTypeKind ? checked((int)type.IntWidth) : 0,
            arithmetic && LLVM.GetNSW(instruction) != 0,
            arithmetic && LLVM.GetNUW(instruction) != 0,
            false,
            0,
            LLVMAtomicOrdering.LLVMAtomicOrderingNotAtomic.ToString(),
            operands)
        {
            IsDisjoint = opcode == LLVMOpcode.LLVMOr ? LLVM.GetIsDisjoint(instruction) != 0 : null
        };
    }

    private static LlvmImportResultV1? ValidateType(
        LLVMTypeRef type,
        HashSet<LLVMTypeRef> types,
        LlvmImporterLimitsV1 limits,
        string source)
    {
        if (type == default)
            return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0026", "LLVM type identity is absent.", source);
        if (!types.Add(type)) return null;
        if (types.Count > limits.MaximumTypes)
            return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0027", "LLVM type budget was exceeded.", source);

        switch (type.Kind)
        {
            case LLVMTypeKind.LLVMVoidTypeKind:
            case LLVMTypeKind.LLVMLabelTypeKind:
                return null;
            case LLVMTypeKind.LLVMIntegerTypeKind:
                return type.IntWidth is 1 or 8 or 16 or 32 or 64
                    ? null
                    : Failure(LlvmImportStatus.Unsupported, "HCLL0026", $"LLVM integer width {type.IntWidth} is not supported.", source);
            case LLVMTypeKind.LLVMPointerTypeKind:
                return type.PointerAddressSpace == 0
                    ? null
                    : Failure(LlvmImportStatus.Unsupported, "HCLL0024", $"LLVM address space {type.PointerAddressSpace} is not supported.", source);
            case LLVMTypeKind.LLVMFunctionTypeKind:
            {
                LlvmImportResultV1? returnFailure = ValidateType(type.ReturnType, types, limits, source);
                if (returnFailure is not null) return returnFailure;
                foreach (LLVMTypeRef parameter in type.GetParamTypes())
                {
                    LlvmImportResultV1? parameterFailure = ValidateType(parameter, types, limits, source);
                    if (parameterFailure is not null) return parameterFailure;
                }
                return null;
            }
            default:
                return Failure(LlvmImportStatus.Unsupported, "HCLL0026", $"LLVM type kind '{type.Kind}' is not supported by the Phase 09 firewall.", source);
        }
    }

    private static LlvmImportResultV1? ValidateMetadataAndProducer(
        LLVMModuleRef module,
        string source,
        out string producer)
    {
        producer = string.Empty;
        int namedMetadataCount = 0;
        for (LLVMOpaqueNamedMDNode* node = LLVM.GetFirstNamedMetadata(module);
             node != null;
             node = LLVM.GetNextNamedMetadata(node))
        {
            if (++namedMetadataCount > LlvmToolchainContractV1.Default.Limits.MaximumMetadataNodes)
                return Failure(LlvmImportStatus.BudgetExhausted, "HCLL0019", "LLVM metadata budget was exceeded.", source);
            nuint length = 0;
            sbyte* namePointer = LLVM.GetNamedMetadataName(node, &length);
            if (namePointer == null || length == 0 || length > 256)
                return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0018", "LLVM named metadata identity is invalid.", source);
            string name = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>((byte*)namePointer, checked((int)length)));
            if (!string.Equals(name, "llvm.ident", StringComparison.Ordinal))
                return Failure(LlvmImportStatus.Unsupported, "HCLL0018", $"LLVM named metadata '{name}' is not supported by the Phase 09 firewall.", source);
        }

        LLVMValueRef[] identifiers = module.GetNamedMetadataOperands("llvm.ident");
        if (identifiers.Length != 1 || identifiers.Length > LlvmToolchainContractV1.Default.Limits.MaximumMetadataNodes)
            return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0020", "Exactly one llvm.ident producer record is required.", source);
        LLVMValueRef[] producerNode = identifiers[0].GetMDNodeOperands();
        if (producerNode.Length != 1)
            return Failure(LlvmImportStatus.UnknownSemantics, "HCLL0020", "llvm.ident producer record is malformed.", source);
        producer = producerNode[0].GetMDString(out _);
        LlvmImportResultV1 producerValidation = new LlvmModuleImporterV1().ValidateHeader(
            new(HybridCpuTargetMachineContractV1.TargetTriple, LlvmToolchainContractV1.HybridCpuLlvmDataLayout, producer), source);
        return producerValidation.Status == LlvmImportStatus.Success ? null : producerValidation;
    }

    private static string TakeMessage(ref sbyte* message, string fallback)
    {
        if (message == null) return fallback;
        string value = StableNativeMessage(new string(message), fallback);
        LLVM.DisposeMessage(message);
        message = null;
        return value;
    }

    private static string StableNativeMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message)
            ? fallback
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool IsPinnedProducer(string producer)
    {
        foreach (string prefix in new[] { "LLVM ", "clang version " })
        {
            if (!producer.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string versionAndSuffix = producer[prefix.Length..];
            return string.Equals(versionAndSuffix, LlvmToolchainContractV1.LlvmRelease, StringComparison.Ordinal) ||
                versionAndSuffix.StartsWith(LlvmToolchainContractV1.LlvmRelease + " ", StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsNativeAvailabilityFailure(Exception exception) =>
        exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException;

    private static LlvmImportResultV1 Failure(LlvmImportStatus status, string code, string message, string? source = null) =>
        new(status, null, [new(status, code, message, source)]);
}
