#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Bindings;
using CircuitWeave.Contracts;
using CircuitWeave.Frontend.Roslyn;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Partial word-operation dispatcher. Supported=false means result is unspecified by the source Compute contract.</summary>
public sealed record BridgeWordDispatchCandidate(HwValidatedDesign Design, ScalarWordDispatchEvidence Observation,
    IReadOnlyList<BridgeHwirAddWordResult> WordCandidates);

public static class BridgeHwirWordDispatch
{
    public static BridgeWordDispatchCandidate Compile(byte[] alu, byte[] opcodeEnum, string aluSha, string enumSha,
        string baseCommit, HardwareBindingEntry addBinding, HardwareBindingsToolchain toolchain, bool includeDirect = false, bool includeShifts = false, bool includeExtensions = false, bool includeNarrow = false, bool includeAddress = false, bool includeLogic = false, bool includeImmediate = false, bool includeWordDivRem = false, bool includeCarryless = false, bool includeHighMultiply = false, bool includeWideDivRem = false, bool includeSignedRemainder = false)
    {
        if ((includeShifts && !includeDirect) || (includeExtensions && !includeShifts) || (includeNarrow && !includeExtensions) || (includeAddress && !includeNarrow) || (includeLogic && !includeAddress) || (includeImmediate && !includeLogic) || (includeWordDivRem && !includeImmediate) || (includeCarryless && !includeWordDivRem) || (includeHighMultiply && !includeCarryless) || (includeWideDivRem && !includeHighMultiply) || (includeSignedRemainder && !includeWideDivRem))
            throw new InvalidDataException("Dispatch modes must include all prerequisite modes.");
        var bridge = new BridgeHwirAddWordCompiler();
        var observation = includeDirect ? ScalarWordDispatchReader.Read(alu, opcodeEnum, aluSha, enumSha, true, includeShifts, includeExtensions, includeNarrow, includeAddress, includeLogic, includeImmediate, includeWordDivRem, includeCarryless, includeHighMultiply, includeWideDivRem, includeSignedRemainder) :
            bridge.ObserveCapturedDispatch(alu, opcodeEnum, aluSha, enumSha);
        var add = addBinding with { Source = addBinding.Source with { Commit = baseCommit } };
        var sub = add with { BindingId = "hybridcpu.scalar.subword.v1", Source = add.Source with { Member = "SubWord" },
            Hardware = add.Hardware with { ModuleName = "scalar_subw", SemanticId = "scalar.subw.signextend32.wrap.v1" } };
        var mul = add with { BindingId = "hybridcpu.scalar.multiplyword.v1", Source = add.Source with { Member = "MultiplyWord" },
            Hardware = add.Hardware with { ModuleName = "scalar_mulw", SemanticId = "scalar.mulw.signextend32.wrap.v1" } };
        BridgeHwirAddWordResult[] candidates = [bridge.CompileCapturedWord(alu, add, toolchain, false, baseCommit, aluSha),
            bridge.CompileCapturedWord(alu, sub, toolchain, true, baseCommit, aluSha),
            bridge.CompileCapturedMultiplyWord(alu, mul, toolchain, baseCommit, aluSha)];
        var u1 = new HwBitVectorType(1); var u16 = new HwBitVectorType(16); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        HwPortContract[] ports = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64),
            new("opcode", HwPortDirection.Input, u32), new("result", HwPortDirection.Output, u64), new("supported", HwPortDirection.Output, u1)];
        var fatalCodeType = new HwBitVectorType(8);
        if (includeSignedRemainder) ports = [.. ports, new("fatal_valid", HwPortDirection.Output, u1), new("fatal_code", HwPortDirection.Output, fatalCodeType)];
        if (includeImmediate) ports = [.. ports, new("immediate", HwPortDirection.Input, u64), new("instructionPc", HwPortDirection.Input, u64)];
        var source = candidates[0].Candidate.ValidatedHwir.Design.Modules.Single().Origin!;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dispatch-observation/v1|" + aluSha + "|" + enumSha))).ToLowerInvariant();
        var origin = source with { Assembly = "CircuitWeave.Captured.WordDispatch.Projection", Member = "Compute", IdentityDigest = identity, Line = observation.ComputeLine, Column = observation.ComputeColumn };
        var nodes = new List<HwNode> { new HwOperationNode("opcode16", "trunc", ["opcode"], u16, Origin: origin),
            new HwConstantNode("zero", u64, new string('0', 64), origin) };
        var instances = new List<HwInstanceNode>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal) { ["AddWord"] = "add", ["SubWord"] = "sub", ["MultiplyWord"] = "mul" };
        var moduleNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["AddWord"] = "scalar_addw", ["SubWord"] = "scalar_subw", ["MultiplyWord"] = "scalar_mulw" };
        var shifts = new Dictionary<string, string>(StringComparer.Ordinal) { ["ShiftLeftWord"] = "shl32_mask5", ["ShiftRightLogicalWord"] = "lshr32_mask5", ["ShiftRightArithmeticWord"] = "ashr32_mask5", ["ShiftLeftWordImmediate"] = "shl32_mask5", ["ShiftRightLogicalWordImmediate"] = "lshr32_mask5", ["ShiftRightArithmeticWordImmediate"] = "ashr32_mask5" };
        var addressMethods = new HashSet<string>(StringComparer.Ordinal) { "ShiftLeftOneAdd", "ShiftLeftTwoAdd", "ShiftLeftThreeAdd", "AddUnsignedWord", "ShiftLeftOneAddUnsignedWord", "ShiftLeftTwoAddUnsignedWord", "ShiftLeftThreeAddUnsignedWord" };
        var logicMethods = new HashSet<string>(StringComparer.Ordinal) { "ConditionalZeroIfEqualZero", "ConditionalZeroIfNotEqualZero", "AndNot64", "OrNot64", "Xnor64" };
        var bitMethods = new HashSet<string>(StringComparer.Ordinal) { "BitSet64", "BitClear64", "BitInvert64", "BitExtract64" };
        var compareMethods = new HashSet<string>(StringComparer.Ordinal) { "MinSigned64", "MaxSigned64", "MinUnsigned64", "MaxUnsigned64" };
        var countMethods = new Dictionary<string, string>(StringComparer.Ordinal) { ["CountLeadingZeros64"] = "clz64", ["CountTrailingZeros64"] = "ctz64", ["CountPopulation64"] = "popcount64" };
        var bitTransformMethods = new Dictionary<string, string>(StringComparer.Ordinal) { ["ReverseBytes64"] = "revbytes64", ["ReverseBitsInEachByte64"] = "brev8_64" };
        var wordDivRemMethods = new HashSet<string>(StringComparer.Ordinal) { "DivideWord", "DivideUnsignedWord", "RemainderWord", "RemainderUnsignedWord" };
        var carrylessMethods = new HashSet<string>(StringComparer.Ordinal) { "CarryLessMultiplyLow64", "CarryLessMultiplyHigh64", "CarryLessMultiplyReverse64" };
        var highMultiplyMethods = new HashSet<string>(StringComparer.Ordinal) { "highmul:MULH", "highmul:MULHU", "highmul:MULHSU" };
        var wideDivRemMethods = new HashSet<string>(StringComparer.Ordinal) { "wide:DIV", "wide:DIVU", "wide:REMU", "wide:REM" };
        var shiftModules = new List<HwModule>();
        static bool IsExtension(string method) => method is "SignExtendWordFromScalar" or "ZeroExtendWordFromScalar" or "SignExtendByteFromScalar" or "SignExtendHalfFromScalar" or "ZeroExtendHalfFromScalar";
        foreach (var arm in observation.Arms)
        {
            var direct = arm.TargetMethod.StartsWith("direct:", StringComparison.Ordinal);
            var isRotate = arm.TargetMethod is "RotateLeft64" or "RotateRight64";
            var isBaseShift = arm.TargetMethod is "ShiftLeft64Immediate" or "ShiftRightLogical64Immediate" or "ShiftRightArithmetic64";
            var isBaseCompare = arm.TargetMethod is "CompareLessSigned64" or "CompareLessUnsigned64";
            var isBaseImmediate = arm.TargetMethod.StartsWith("base:", StringComparison.Ordinal);
            var isWordImmediateAdd = arm.TargetMethod == "AddWordImmediate";
            var isCount = countMethods.TryGetValue(arm.TargetMethod, out var countOpcode);
            var isBitTransform = bitTransformMethods.TryGetValue(arm.TargetMethod, out var bitTransformOpcode);
            var isRawNot = arm.TargetMethod == "base:NOT";
            var isShift = shifts.TryGetValue(arm.TargetMethod, out var shiftOp);
            var isExtension = IsExtension(arm.TargetMethod); var isAddress = addressMethods.Contains(arm.TargetMethod); var isLogic = logicMethods.Contains(arm.TargetMethod); var isBit = bitMethods.Contains(arm.TargetMethod); var isCompare = compareMethods.Contains(arm.TargetMethod); var isImmediate = arm.TargetMethod == "ShiftLeftUnsignedWordImmediate";
            var isWordDivRem = wordDivRemMethods.Contains(arm.TargetMethod);
            var isCarryless = carrylessMethods.Contains(arm.TargetMethod);
            var isHighMultiply = highMultiplyMethods.Contains(arm.TargetMethod);
            var isWideDivRem = wideDivRemMethods.Contains(arm.TargetMethod);
            var id = direct ? "direct_" + arm.TargetMethod[7..] : isRawNot ? "raw52_not" : isRotate ? "rotate_" + arm.EnumMember.ToLowerInvariant() : isBaseShift || isBaseCompare || isBaseImmediate ? "base_" + arm.EnumMember.ToLowerInvariant() : isShift ? "shift_" + arm.EnumMember.ToLowerInvariant() : isExtension ? "extension_" + arm.EnumMember.ToLowerInvariant() : isAddress ? "address_" + arm.EnumMember.ToLowerInvariant() : isLogic ? "logic_" + arm.EnumMember.ToLowerInvariant() : isBit ? "bit_" + arm.EnumMember.ToLowerInvariant() : isCompare ? "compare_" + arm.EnumMember.ToLowerInvariant() : isImmediate ? "immediate_slli_uw" : isWordImmediateAdd ? "immediate_addiw" : isCount ? "count_" + arm.EnumMember.ToLowerInvariant() : isBitTransform ? "transform_" + arm.EnumMember.ToLowerInvariant() : isWordDivRem ? "word_divrem_" + arm.EnumMember.ToLowerInvariant() : isCarryless ? "carryless_" + arm.EnumMember.ToLowerInvariant() : isHighMultiply ? "highmul_" + arm.EnumMember.ToLowerInvariant() : isWideDivRem ? "wide_divrem_" + arm.EnumMember.ToLowerInvariant() : ids[arm.TargetMethod];
            nodes.Add(new HwConstantNode(id + "_opcode", u16, Convert.ToString(arm.Opcode, 2).PadLeft(16, '0'), origin));
            nodes.Add(new HwOperationNode(id + "_match", "eq", ["opcode16", id + "_opcode"], u1, Origin: origin));
            if (direct) nodes.Add(new HwOperationNode(id + "_value", arm.TargetMethod[7..], ["op1", "op2"], u64, Origin: origin));
            else if (isWordDivRem)
            {
                var module = BuildWordDivRemModule(arm.EnumMember, arm.TargetMethod, origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("word-divrem-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn });
                shiftModules.Add(module);
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", module.Id, new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isCarryless)
            {
                var module = BuildCarrylessModule(arm.EnumMember, arm.TargetMethod, origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("carryless-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn });
                shiftModules.Add(module);
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", module.Id, new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isHighMultiply)
            {
                var module = BuildHighMultiplyModule(arm.EnumMember, arm.TargetMethod, origin with { Member = "Compute", IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("highmul-observation/v1|" + aluSha + "|" + arm.EnumMember))).ToLowerInvariant(), Line = arm.Line });
                shiftModules.Add(module);
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", module.Id, new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isWideDivRem)
            {
                bool signedRem = arm.TargetMethod == "wide:REM";
                var moduleOrigin = origin with { Member = "Compute", IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wide-divrem-observation/v1|" + aluSha + "|" + arm.EnumMember))).ToLowerInvariant(), Line = arm.Line };
                var module = signedRem ? BuildSignedRemainderModule(arm.EnumMember, moduleOrigin) : BuildWideDivRemModule(arm.EnumMember, arm.TargetMethod, moduleOrigin);
                shiftModules.Add(module);
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                var connections = new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" };
                if (signedRem)
                {
                    nodes.Add(new HwWireNode(id + "_fatal", u1, origin)); nodes.Add(new HwWireNode(id + "_fatal_code", fatalCodeType, origin));
                    connections["fatal_valid"] = id + "_fatal"; connections["fatal_code"] = id + "_fatal_code";
                }
                instances.Add(new HwInstanceNode(id + "_instance", module.Id, connections, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isRotate)
            {
                var rotateOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rotate-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                bool immediateRotate = arm.EnumMember is "ROLI" or "RORI";
                string amountPort = immediateRotate ? "immediate" : "op2";
                HwPortContract[] rotatePorts = [new("op1", HwPortDirection.Input, u64), new(amountPort, HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                HwNode[] rotateNodes =
                [
                    new HwConstantNode("sixty_four", u64, Convert.ToString(64, 2).PadLeft(64, '0'), rotateOrigin),
                    new HwOperationNode("complement_amount", "sub", ["sixty_four", amountPort], u64, Origin: rotateOrigin),
                    new HwOperationNode("shift_low", arm.EnumMember is "ROL" or "ROLI" ? "shl64_mask6" : "lshr64_mask6", ["op1", amountPort], u64, Origin: rotateOrigin),
                    new HwOperationNode("shift_high", arm.EnumMember is "ROL" or "ROLI" ? "lshr64_mask6" : "shl64_mask6", ["op1", "complement_amount"], u64, Origin: rotateOrigin),
                    new HwOperationNode("value", "or", ["shift_low", "shift_high"], u64, Origin: rotateOrigin),
                    new HwOutputDriveNode("drive", "result", "value", rotateOrigin)
                ];
                var rotateName = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(rotateName, rotateName, rotatePorts, rotateNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, rotatePorts, new HwTimingContract(0, 1)), rotateOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", rotateName, new Dictionary<string,string> { ["op1"] = "op1", [amountPort] = amountPort, ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isBaseShift || isBaseCompare)
            {
                var baseOrigin = origin with { Member = "Compute", IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("base-integer-observation/v1|" + aluSha + "|" + arm.EnumMember))).ToLowerInvariant(), Line = arm.Line };
                bool usesImmediate = arm.TargetMethod is "ShiftLeft64Immediate" or "ShiftRightLogical64Immediate";
                var secondPort = usesImmediate ? "immediate" : "op2";
                HwPortContract[] basePorts = [new("op1", HwPortDirection.Input, u64), new(secondPort, HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var baseNodes = new List<HwNode>();
                if (isBaseShift)
                {
                    string operation = arm.TargetMethod switch
                    {
                        "ShiftLeft64Immediate" => "shl64_mask6",
                        "ShiftRightLogical64Immediate" => "lshr64_mask6",
                        "ShiftRightArithmetic64" => "ashr64_mask6",
                        _ => throw new InvalidDataException("Unsupported base shift target.")
                    };
                    baseNodes.Add(new HwOperationNode("value", operation, ["op1", secondPort], u64, Origin: baseOrigin));
                }
                else
                {
                    bool signedCompare = arm.TargetMethod == "CompareLessSigned64";
                    if (signedCompare)
                    {
                        var signed64 = new HwBitVectorType(64, HwSignedness.Signed);
                        baseNodes.Add(new HwOperationNode("op1_signed", "bitcast", ["op1"], signed64, Origin: baseOrigin));
                        baseNodes.Add(new HwOperationNode("op2_signed", "bitcast", ["op2"], signed64, Origin: baseOrigin));
                    }
                    string operation = signedCompare ? "slt64" : "ult64";
                    string left = signedCompare ? "op1_signed" : "op1";
                    string right = signedCompare ? "op2_signed" : "op2";
                    baseNodes.Add(new HwOperationNode("less", operation, [left, right], u1, Origin: baseOrigin));
                    baseNodes.Add(new HwOperationNode("value", "zext", ["less"], u64, Origin: baseOrigin));
                }
                baseNodes.Add(new HwOutputDriveNode("drive", "result", "value", baseOrigin));
                var name = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(name, name, basePorts, baseNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, basePorts, new HwTimingContract(0,1)), baseOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", name, new Dictionary<string,string> { ["op1"] = "op1", [secondPort] = secondPort, ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isBaseImmediate && !isRawNot)
            {
                var immediateOrigin = origin with { Member = "Compute", IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("base-immediate-observation/v1|" + aluSha + "|" + arm.EnumMember))).ToLowerInvariant(), Line = arm.Line };
                string operation = arm.EnumMember switch
                {
                    "ADDI" => "add",
                    "ANDI" => "and",
                    "ORI" => "or",
                    "XORI" => "xor",
                    "SLTI" => "slt64",
                    "SLTIU" => "ult64",
                    "SLLI" => "shl64_mask6",
                    "SRLI" => "lshr64_mask6",
                    "SRAI" => "ashr64_mask6",
                    "LUI" => "mul",
                    "AUIPC" => "add",
                    _ => throw new InvalidDataException("Unsupported base immediate opcode.")
                };
                HwPortContract[] immediatePorts = arm.EnumMember == "AUIPC"
                    ? [new("immediate", HwPortDirection.Input, u64), new("instructionPc", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)]
                    : [new("op1", HwPortDirection.Input, u64), new("immediate", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                HwNode[] immediateNodes = arm.EnumMember is "LUI" or "AUIPC"
                    ? arm.EnumMember == "LUI"
                        ? [new HwConstantNode("upper_imm_scale", u64, Convert.ToString(4096, 2).PadLeft(64, '0'), immediateOrigin), new HwOperationNode("value", "mul", ["immediate", "upper_imm_scale"], u64, Origin: immediateOrigin), new HwOutputDriveNode("drive", "result", "value", immediateOrigin)]
                        : [new HwConstantNode("upper_imm_scale", u64, Convert.ToString(4096, 2).PadLeft(64, '0'), immediateOrigin), new HwOperationNode("upper_immediate", "mul", ["immediate", "upper_imm_scale"], u64, Origin: immediateOrigin), new HwOperationNode("value", "add", ["instructionPc", "upper_immediate"], u64, Origin: immediateOrigin), new HwOutputDriveNode("drive", "result", "value", immediateOrigin)]
                    : operation is "slt64" or "ult64"
                    ? operation == "slt64"
                        ? [new HwOperationNode("op1_signed", "bitcast", ["op1"], new HwBitVectorType(64, HwSignedness.Signed), Origin: immediateOrigin), new HwOperationNode("immediate_signed", "bitcast", ["immediate"], new HwBitVectorType(64, HwSignedness.Signed), Origin: immediateOrigin), new HwOperationNode("less", operation, ["op1_signed", "immediate_signed"], u1, Origin: immediateOrigin), new HwOperationNode("value", "zext", ["less"], u64, Origin: immediateOrigin), new HwOutputDriveNode("drive", "result", "value", immediateOrigin)]
                        : [new HwOperationNode("less", operation, ["op1", "immediate"], u1, Origin: immediateOrigin), new HwOperationNode("value", "zext", ["less"], u64, Origin: immediateOrigin), new HwOutputDriveNode("drive", "result", "value", immediateOrigin)]
                    : [new HwOperationNode("value", operation, ["op1", "immediate"], u64, Origin: immediateOrigin), new HwOutputDriveNode("drive", "result", "value", immediateOrigin)];
                var immediateName = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(immediateName, immediateName, immediatePorts, immediateNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, immediatePorts, new HwTimingContract(0, 1)), immediateOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", immediateName, arm.EnumMember == "AUIPC"
                    ? new Dictionary<string,string> { ["immediate"] = "immediate", ["instructionPc"] = "instructionPc", ["result"] = id + "_value" }
                    : new Dictionary<string,string> { ["op1"] = "op1", ["immediate"] = "immediate", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isShift)
            {
                var immediateShift = arm.TargetMethod.EndsWith("Immediate", StringComparison.Ordinal);
                var shiftIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("shift-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant();
                var shiftOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = shiftIdentity, Line = arm.MethodLine, Column = arm.MethodColumn };
                var s32 = new HwBitVectorType(32, HwSignedness.Signed); var s64 = new HwBitVectorType(64, HwSignedness.Signed);
                var leafPorts = new HwPortContract[] { new("op1", HwPortDirection.Input, u64), new(immediateShift ? "immediate" : "op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64) };
                HwNode[] shiftNodes = [new HwOperationNode("data", "trunc", ["op1"], u32, Origin: shiftOrigin),
                    new HwOperationNode("amount", "trunc", [immediateShift ? "immediate" : "op2"], u32, Origin: shiftOrigin),
                    new HwOperationNode("bits", shiftOp!, ["data", "amount"], u32, Origin: shiftOrigin),
                    new HwOperationNode("signed_bits", "bitcast", ["bits"], s32, Origin: shiftOrigin),
                    new HwOperationNode("extend", "sext", ["signed_bits"], s64, Origin: shiftOrigin),
                    new HwOperationNode("value", "bitcast", ["extend"], u64, Origin: shiftOrigin),
                    new HwOutputDriveNode("drive", "result", "value", shiftOrigin)];
                var shiftName = "scalar_" + arm.EnumMember.ToLowerInvariant();
                var shiftContract = new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1));
                shiftModules.Add(new HwModule(shiftName, shiftName, leafPorts, shiftNodes, [], shiftContract, shiftOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", shiftName,
                    new Dictionary<string,string> { ["op1"] = "op1", [immediateShift ? "immediate" : "op2"] = immediateShift ? "immediate" : "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isImmediate)
            {
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("immediate-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant();
                var leafOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = digest, Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new("immediate", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                HwNode[] leafNodes = [new HwOperationNode("low", "trunc", ["op1"], u32, Origin: leafOrigin),
                    new HwOperationNode("extended", "zext", ["low"], u64, Origin: leafOrigin),
                    new HwOperationNode("value", "shl64_mask6", ["extended", "immediate"], u64, Origin: leafOrigin),
                    new HwOutputDriveNode("drive", "result", "value", leafOrigin)];
                var leafContract = new HwHardwareContract("scalar.slli_uw.partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1));
                shiftModules.Add(new HwModule("scalar_slli_uw", "scalar_slli_uw", leafPorts, leafNodes, [], leafContract, leafOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", "scalar_slli_uw", new Dictionary<string,string> { ["op1"] = "op1", ["immediate"] = "immediate", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isWordImmediateAdd)
            {
                var wordAddOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("addiw-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] wordAddPorts = [new("op1", HwPortDirection.Input, u64), new("immediate", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var signed32 = new HwBitVectorType(32, HwSignedness.Signed); var signed64 = new HwBitVectorType(64, HwSignedness.Signed);
                HwNode[] wordAddNodes = [new HwOperationNode("op1_low", "trunc", ["op1"], u32, Origin: wordAddOrigin),
                    new HwOperationNode("immediate_low", "trunc", ["immediate"], u32, Origin: wordAddOrigin),
                    new HwOperationNode("sum", "add", ["op1_low", "immediate_low"], u32, Origin: wordAddOrigin),
                    new HwOperationNode("signed_sum", "bitcast", ["sum"], signed32, Origin: wordAddOrigin),
                    new HwOperationNode("extended_sum", "sext", ["signed_sum"], signed64, Origin: wordAddOrigin),
                    new HwOperationNode("value", "bitcast", ["extended_sum"], u64, Origin: wordAddOrigin), new HwOutputDriveNode("drive", "result", "value", wordAddOrigin)];
                shiftModules.Add(new HwModule("scalar_addiw", "scalar_addiw", wordAddPorts, wordAddNodes, [], new HwHardwareContract("scalar.addiw.partial.v1", HwModuleKind.Combinational, wordAddPorts, new HwTimingContract(0, 1)), wordAddOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", "scalar_addiw", new Dictionary<string,string> { ["op1"] = "op1", ["immediate"] = "immediate", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isCount)
            {
                var countOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("count-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] countPorts = [new("op1", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                HwNode[] countNodes = [new HwOperationNode("value", countOpcode!, ["op1"], u64, Origin: countOrigin), new HwOutputDriveNode("drive", "result", "value", countOrigin)];
                var countName = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(countName, countName, countPorts, countNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, countPorts, new HwTimingContract(0, 1)), countOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", countName, new Dictionary<string,string> { ["op1"] = "op1", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isRawNot)
            {
                var notOrigin = origin with { Member = "Compute", IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("raw-opcode-52-not/v1|" + aluSha))).ToLowerInvariant(), Line = arm.Line };
                HwPortContract[] notPorts = [new("op1", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                HwNode[] notNodes = [new HwConstantNode("all_ones", u64, new string('1', 64), notOrigin), new HwOperationNode("value", "xor", ["op1", "all_ones"], u64, Origin: notOrigin), new HwOutputDriveNode("drive", "result", "value", notOrigin)];
                shiftModules.Add(new HwModule("scalar_not", "scalar_not", notPorts, notNodes, [], new HwHardwareContract("scalar.not.raw52.partial.v1", HwModuleKind.Combinational, notPorts, new HwTimingContract(0, 1)), notOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", "scalar_not", new Dictionary<string,string> { ["op1"] = "op1", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isBitTransform)
            {
                var transformOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bit-transform-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] transformPorts = [new("op1", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                HwNode[] transformNodes = [new HwOperationNode("value", bitTransformOpcode!, ["op1"], u64, Origin: transformOrigin), new HwOutputDriveNode("drive", "result", "value", transformOrigin)];
                var transformName = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(transformName, transformName, transformPorts, transformNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, transformPorts, new HwTimingContract(0, 1)), transformOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", transformName, new Dictionary<string,string> { ["op1"] = "op1", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isCompare)
            {
                var compareOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("compare-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var isMin = arm.EnumMember.StartsWith("MIN", StringComparison.Ordinal);
                var signed = !arm.EnumMember.EndsWith("U", StringComparison.Ordinal);
                string compareLeft = isMin ? "op2" : "op1";
                string compareRight = isMin ? "op1" : "op2";
                var compareNodes = new List<HwNode>();
                if (signed)
                {
                    var signed64 = new HwBitVectorType(64, HwSignedness.Signed);
                    compareNodes.Add(new HwOperationNode("compare_left_signed", "bitcast", [compareLeft], signed64, Origin: compareOrigin));
                    compareNodes.Add(new HwOperationNode("compare_right_signed", "bitcast", [compareRight], signed64, Origin: compareOrigin));
                    compareLeft = "compare_left_signed";
                    compareRight = "compare_right_signed";
                }
                compareNodes.Add(new HwOperationNode("less", signed ? "slt64" : "ult64", [compareLeft, compareRight], u1, Origin: compareOrigin));
                compareNodes.Add(new HwMuxNode("value", "less", "op2", "op1", u64, compareOrigin));
                compareNodes.Add(new HwOutputDriveNode("drive", "result", "value", compareOrigin));
                var name = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(name, name, leafPorts, compareNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1)), compareOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", name, new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isBit)
            {
                var bitOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bit-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                var input = arm.EnumMember.EndsWith("I", StringComparison.Ordinal) ? "immediate" : "op2";
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new(input, HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var bitNodes = new List<HwNode> { new HwConstantNode("one", u64, new string('0', 63) + "1", bitOrigin), new HwOperationNode("mask", arm.TargetMethod == "BitExtract64" ? "lshr64_mask6" : "shl64_mask6", [arm.TargetMethod == "BitExtract64" ? "op1" : "one", input], u64, Origin: bitOrigin) };
                var mask = "mask";
                if (arm.TargetMethod == "BitClear64") { bitNodes.Add(new HwConstantNode("ones", u64, new string('1', 64), bitOrigin)); bitNodes.Add(new HwOperationNode("inverted", "xor", ["mask", "ones"], u64, Origin: bitOrigin)); mask = "inverted"; }
                bitNodes.Add(new HwOperationNode("value", arm.TargetMethod == "BitSet64" ? "or" : arm.TargetMethod is "BitClear64" or "BitExtract64" ? "and" : "xor", [arm.TargetMethod == "BitExtract64" ? "one" : "op1", mask], u64, Origin: bitOrigin));
                bitNodes.Add(new HwOutputDriveNode("drive", "result", "value", bitOrigin));
                var name = "scalar_" + arm.EnumMember.ToLowerInvariant();
                shiftModules.Add(new HwModule(name, name, leafPorts, bitNodes, [], new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1)), bitOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", name, new Dictionary<string,string> { ["op1"] = "op1", [input] = input, ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isLogic)
            {
                var logicIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("logic-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant();
                var logicOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = logicIdentity, Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var logicNodes = new List<HwNode>();
                if (arm.EnumMember.StartsWith("CZERO_", StringComparison.Ordinal))
                {
                    logicNodes.Add(new HwConstantNode("zero", u64, new string('0', 64), logicOrigin));
                    logicNodes.Add(new HwOperationNode("is_zero", "eq", ["op2", "zero"], u1, Origin: logicOrigin));
                    logicNodes.Add(new HwMuxNode("value", "is_zero", arm.EnumMember == "CZERO_EQZ" ? "zero" : "op1", arm.EnumMember == "CZERO_EQZ" ? "op1" : "zero", u64, logicOrigin));
                }
                else
                {
                    logicNodes.Add(new HwConstantNode("ones", u64, new string('1', 64), logicOrigin));
                    if (arm.EnumMember == "XNOR")
                    {
                        logicNodes.Add(new HwOperationNode("bits", "xor", ["op1", "op2"], u64, Origin: logicOrigin));
                        logicNodes.Add(new HwOperationNode("value", "xor", ["bits", "ones"], u64, Origin: logicOrigin));
                    }
                    else
                    {
                        logicNodes.Add(new HwOperationNode("inverted", "xor", ["op2", "ones"], u64, Origin: logicOrigin));
                        logicNodes.Add(new HwOperationNode("value", arm.EnumMember == "ANDN" ? "and" : "or", ["op1", "inverted"], u64, Origin: logicOrigin));
                    }
                }
                logicNodes.Add(new HwOutputDriveNode("drive", "result", "value", logicOrigin));
                var name = "scalar_" + arm.EnumMember.ToLowerInvariant();
                var leafContract = new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1));
                shiftModules.Add(new HwModule(name, name, leafPorts, logicNodes, [], leafContract, logicOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", name, new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isAddress)
            {
                var addressIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("address-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant();
                var addressOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = addressIdentity, Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var addressNodes = new List<HwNode>(); var input = "op1";
                if (arm.EnumMember.EndsWith("_UW", StringComparison.Ordinal))
                {
                    addressNodes.Add(new HwOperationNode("low", "trunc", ["op1"], u32, Origin: addressOrigin));
                    addressNodes.Add(new HwOperationNode("extended", "zext", ["low"], u64, Origin: addressOrigin)); input = "extended";
                }
                var count = arm.EnumMember.StartsWith("SH1", StringComparison.Ordinal) ? 1 : arm.EnumMember.StartsWith("SH2", StringComparison.Ordinal) ? 2 : arm.EnumMember.StartsWith("SH3", StringComparison.Ordinal) ? 3 : 0;
                for (var i = 0; i < count; i++) { var next = "double" + i; addressNodes.Add(new HwOperationNode(next, "add", [input, input], u64, Origin: addressOrigin)); input = next; }
                addressNodes.Add(new HwOperationNode("value", "add", [input, "op2"], u64, Origin: addressOrigin));
                addressNodes.Add(new HwOutputDriveNode("drive", "result", "value", addressOrigin));
                var name = "scalar_" + arm.EnumMember.ToLowerInvariant();
                var leafContract = new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1));
                shiftModules.Add(new HwModule(name, name, leafPorts, addressNodes, [], leafContract, addressOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", name, new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else if (isExtension)
            {
                var extensionIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("extension-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant();
                var extensionOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = extensionIdentity, Line = arm.MethodLine, Column = arm.MethodColumn };
                var width = arm.EnumMember.EndsWith("_B", StringComparison.Ordinal) ? 8 : arm.EnumMember.EndsWith("_H", StringComparison.Ordinal) ? 16 : 32;
                var narrowType = new HwBitVectorType(width); var signedType = new HwBitVectorType(width, HwSignedness.Signed); var s64 = new HwBitVectorType(64, HwSignedness.Signed);
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var extensionNodes = new List<HwNode> { new HwOperationNode("bits", "trunc", ["op1"], narrowType, Origin: extensionOrigin) };
                if (arm.EnumMember.StartsWith("SEXT_", StringComparison.Ordinal))
                {
                    extensionNodes.Add(new HwOperationNode("signed_bits", "bitcast", ["bits"], signedType, Origin: extensionOrigin));
                    extensionNodes.Add(new HwOperationNode("extend", "sext", ["signed_bits"], s64, Origin: extensionOrigin));
                    extensionNodes.Add(new HwOperationNode("value", "bitcast", ["extend"], u64, Origin: extensionOrigin));
                }
                else extensionNodes.Add(new HwOperationNode("value", "zext", ["bits"], u64, Origin: extensionOrigin));
                extensionNodes.Add(new HwOutputDriveNode("drive", "result", "value", extensionOrigin));
                var name = "scalar_" + arm.EnumMember.ToLowerInvariant();
                var leafContract = new HwHardwareContract("scalar." + arm.EnumMember.ToLowerInvariant() + ".partial.v1", HwModuleKind.Combinational, leafPorts, new HwTimingContract(0,1));
                shiftModules.Add(new HwModule(name, name, leafPorts, extensionNodes, [], leafContract, extensionOrigin));
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", name,
                    new Dictionary<string,string> { ["op1"] = "op1", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
            else
            {
                nodes.Add(new HwWireNode(id + "_value", u64, origin));
                instances.Add(new HwInstanceNode(id + "_instance", moduleNames[arm.TargetMethod],
                    new Dictionary<string,string> { ["op1"] = "op1", ["op2"] = "op2", ["result"] = id + "_value" }, new Dictionary<string,string>(), Origin: origin));
            }
        }
        nodes.Add(new HwOperationNode("sub_or_mul", "or", ["sub_match", "mul_match"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("any_match", "or", ["add_match", "sub_or_mul"], u1, Origin: origin));
        nodes.Add(new HwMuxNode("select_mul", "mul_match", "mul_value", "zero", u64, origin));
        nodes.Add(new HwMuxNode("select_sub", "sub_match", "sub_value", "select_mul", u64, origin));
        nodes.Add(new HwMuxNode("select_add", "add_match", "add_value", "select_sub", u64, origin));
        var resultNode = "select_add"; var supportedNode = "any_match";
        foreach (var arm in observation.Arms.Where(a => a.TargetMethod.StartsWith("direct:", StringComparison.Ordinal) || a.TargetMethod.StartsWith("base:", StringComparison.Ordinal) || shifts.ContainsKey(a.TargetMethod) || IsExtension(a.TargetMethod) || addressMethods.Contains(a.TargetMethod) || logicMethods.Contains(a.TargetMethod) || bitMethods.Contains(a.TargetMethod) || compareMethods.Contains(a.TargetMethod) || countMethods.ContainsKey(a.TargetMethod) || bitTransformMethods.ContainsKey(a.TargetMethod) || wordDivRemMethods.Contains(a.TargetMethod) || carrylessMethods.Contains(a.TargetMethod) || highMultiplyMethods.Contains(a.TargetMethod) || wideDivRemMethods.Contains(a.TargetMethod) || a.TargetMethod is "AddWordImmediate" or "ShiftLeftUnsignedWordImmediate" || a.TargetMethod is "ShiftLeft64Immediate" or "ShiftRightLogical64Immediate" or "ShiftRightArithmetic64" or "CompareLessSigned64" or "CompareLessUnsigned64" or "RotateLeft64" or "RotateRight64"))
        {
            var id = arm.TargetMethod is "RotateLeft64" or "RotateRight64" ? "rotate_" + arm.EnumMember.ToLowerInvariant() : arm.TargetMethod == "base:NOT" ? "raw52_not" : arm.TargetMethod.StartsWith("base:", StringComparison.Ordinal) || arm.TargetMethod is "ShiftLeft64Immediate" or "ShiftRightLogical64Immediate" or "ShiftRightArithmetic64" or "CompareLessSigned64" or "CompareLessUnsigned64" ? "base_" + arm.EnumMember.ToLowerInvariant() : shifts.ContainsKey(arm.TargetMethod) ? "shift_" + arm.EnumMember.ToLowerInvariant() : IsExtension(arm.TargetMethod) ? "extension_" + arm.EnumMember.ToLowerInvariant() : addressMethods.Contains(arm.TargetMethod) ? "address_" + arm.EnumMember.ToLowerInvariant() : logicMethods.Contains(arm.TargetMethod) ? "logic_" + arm.EnumMember.ToLowerInvariant() : bitMethods.Contains(arm.TargetMethod) ? "bit_" + arm.EnumMember.ToLowerInvariant() : compareMethods.Contains(arm.TargetMethod) ? "compare_" + arm.EnumMember.ToLowerInvariant() : countMethods.ContainsKey(arm.TargetMethod) ? "count_" + arm.EnumMember.ToLowerInvariant() : bitTransformMethods.ContainsKey(arm.TargetMethod) ? "transform_" + arm.EnumMember.ToLowerInvariant() : wordDivRemMethods.Contains(arm.TargetMethod) ? "word_divrem_" + arm.EnumMember.ToLowerInvariant() : carrylessMethods.Contains(arm.TargetMethod) ? "carryless_" + arm.EnumMember.ToLowerInvariant() : highMultiplyMethods.Contains(arm.TargetMethod) ? "highmul_" + arm.EnumMember.ToLowerInvariant() : wideDivRemMethods.Contains(arm.TargetMethod) ? "wide_divrem_" + arm.EnumMember.ToLowerInvariant() : arm.TargetMethod == "ShiftLeftUnsignedWordImmediate" ? "immediate_slli_uw" : arm.TargetMethod == "AddWordImmediate" ? "immediate_addiw" : "direct_" + arm.TargetMethod[7..];
            nodes.Add(new HwMuxNode(id + "_select", id + "_match", id + "_value", resultNode, u64, origin));
            nodes.Add(new HwOperationNode(id + "_supported", "or", [supportedNode, id + "_match"], u1, Origin: origin));
            resultNode = id + "_select"; supportedNode = id + "_supported";
        }
        nodes.Add(new HwOutputDriveNode("drive_result", "result", resultNode, origin));
        nodes.Add(new HwOutputDriveNode("drive_supported", "supported", supportedNode, origin));
        if (includeSignedRemainder)
        {
            var remArm = observation.Arms.Single(a => a.EnumMember == "REM");
            var remId = "wide_divrem_rem";
            nodes.Add(new HwOperationNode("fatal_for_rem", "and", [remId + "_match", remId + "_fatal"], u1, Origin: origin));
            nodes.Add(new HwOutputDriveNode("drive_fatal_valid", "fatal_valid", "fatal_for_rem", origin));
            nodes.Add(new HwConstantNode("fatal_code_zero", fatalCodeType, new string('0', 8), origin));
            nodes.Add(new HwMuxNode("fatal_code_for_rem", "fatal_for_rem", remId + "_fatal_code", "fatal_code_zero", fatalCodeType, origin));
            nodes.Add(new HwOutputDriveNode("drive_fatal_code", "fatal_code", "fatal_code_for_rem", origin));
        }
        var contract = new HwHardwareContract("scalar.word-dispatch.partial.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0,1));
        var top = new HwModule("scalar_word_dispatch_partial", "scalar_word_dispatch_partial", ports, nodes, instances, contract, origin);
        var leafDesign = candidates[0].Candidate.ValidatedHwir.Design;
        var design = new HwDesign("hwir/v0.1", top.Id,
            [top, .. candidates.Select(x => x.Candidate.ValidatedHwir.Design.Modules.Single()), .. shiftModules],
            leafDesign.Toolchain with { HardwareLanguageProfile = "hwp-hwir-comb-v1" }, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        if (!validation.Accepted || validation.Validated is null)
            throw new InvalidDataException("Partial word dispatcher HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return new(validation.Validated, observation, candidates);
    }

    private static HwModule BuildWordDivRemModule(string opcodeName, string target, HwSourceOrigin origin)
    {
        var u1 = new HwBitVectorType(1); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var s32 = new HwBitVectorType(32, HwSignedness.Signed); var s64 = new HwBitVectorType(64, HwSignedness.Signed);
        var ports = new[] { new HwPortContract("op1", HwPortDirection.Input, u64), new HwPortContract("op2", HwPortDirection.Input, u64), new HwPortContract("result", HwPortDirection.Output, u64) };
        var nodes = new List<HwNode>();
        void C(string id, ulong value) => nodes.Add(new HwConstantNode(id, u64, Convert.ToString(unchecked((long)value), 2).PadLeft(64, '0'), origin));
        nodes.Add(new HwConstantNode("onebit", u1, "1", origin));
        void O(string id, string op, string a, string b, HwBitVectorType? type = null) => nodes.Add(new HwOperationNode(id, op, [a,b], type ?? u64, Origin: origin));
        nodes.Add(new HwOperationNode("a32", "trunc", ["op1"], u32, Origin: origin));
        nodes.Add(new HwOperationNode("b32", "trunc", ["op2"], u32, Origin: origin));
        nodes.Add(new HwOperationNode("a", "zext", ["a32"], u64, Origin: origin));
        nodes.Add(new HwOperationNode("b", "zext", ["b32"], u64, Origin: origin));
        bool signed = target is "DivideWord" or "RemainderWord";
        bool remainder = target is "RemainderWord" or "RemainderUnsignedWord";
        string dividend = "a", divisor = "b", signA = "zero", signB = "zero";
        C("zero", 0); C("one", 1); C("ones32", uint.MaxValue); C("signbit", 0x80000000); C("mask32", uint.MaxValue);
        if (signed)
        {
            C("not_mask32", ~((ulong)uint.MaxValue));
            O("sign_a_bits", "and", "a", "signbit"); O("sign_b_bits", "and", "b", "signbit");
            nodes.Add(new HwOperationNode("neg_a", "eq", ["sign_a_bits", "signbit"], u1, Origin: origin));
            nodes.Add(new HwOperationNode("neg_b", "eq", ["sign_b_bits", "signbit"], u1, Origin: origin));
            O("neg_a_bits", "xor", "a", "mask32"); O("neg_b_bits", "xor", "b", "mask32");
            O("neg_a_plus", "add", "neg_a_bits", "one"); O("neg_b_plus", "add", "neg_b_bits", "one");
            nodes.Add(new HwMuxNode("magnitude_a", "neg_a", "neg_a_plus", "a", u64, origin));
            nodes.Add(new HwMuxNode("magnitude_b", "neg_b", "neg_b_plus", "b", u64, origin));
            dividend = "magnitude_a"; divisor = "magnitude_b"; signA = "neg_a"; signB = "neg_b";
        }
        string rem = "zero", quot = "zero";
        for (int i = 31; i >= 0; i--)
        {
            string bit = "dividend_bit_" + i, shifted = "rem_shift_" + i, trial = "rem_trial_" + i, less = "trial_less_" + i, nextRem = "rem_next_" + i, qbit = "qbit_" + i, qshift = "qshift_" + i, nextQ = "qnext_" + i;
            C("shift_amount_" + i, (ulong)i);
            O(bit + "_shift", "lshr64_mask6", dividend, "shift_amount_" + i);
            O(bit, "and", bit + "_shift", "one"); O(shifted, "shl64_mask6", rem, "one"); O(trial, "or", shifted, bit);
            O(less, "ult64", trial, divisor, u1);
            O("trial_minus_divisor_" + i, "sub", trial, divisor);
            nodes.Add(new HwMuxNode(nextRem, less, trial, "trial_minus_divisor_" + i, u64, origin));
            nodes.Add(new HwOperationNode(qbit, "xor", [less, "onebit"], u1, Origin: origin));
            O(qshift, "shl64_mask6", quot, "one");
            nodes.Add(new HwOperationNode("qbit64_" + i, "zext", [qbit], u64, Origin: origin));
            O(nextQ, "or", qshift, "qbit64_" + i); rem = nextRem; quot = nextQ;
        }
        string value = remainder ? rem : quot;
        if (signed)
        {
            string sign = remainder ? signA : "quotient_negative";
            if (!remainder)
            {
                nodes.Add(new HwOperationNode("signs_differ", "xor", [signA, signB], u1, Origin: origin));
                sign = "signs_differ";
            }
            O("signed_neg_bits", "xor", value, "mask32"); O("signed_neg_plus", "add", "signed_neg_bits", "one");
            nodes.Add(new HwMuxNode("signed_value", sign, "signed_neg_plus", value, u64, origin)); value = "signed_value";
        }
        // The C# helpers explicitly define divisor-zero outputs. Keep that branch explicit,
        // even though the unsigned restoring network naturally yields quotient=0xffffffff/remainder=dividend.
        O("divisor_is_zero", "eq", "b", "zero", u1);
        if (!remainder) nodes.Add(new HwMuxNode("zero_divisor_value", "divisor_is_zero", "ones32", value, u64, origin));
        else nodes.Add(new HwMuxNode("zero_divisor_value", "divisor_is_zero", "a", value, u64, origin));
        nodes.Add(new HwOperationNode("result32", "trunc", ["zero_divisor_value"], u32, Origin: origin));
        nodes.Add(new HwOperationNode("result_signed32", "bitcast", ["result32"], s32, Origin: origin));
        nodes.Add(new HwOperationNode("result_signed64", "sext", ["result_signed32"], s64, Origin: origin));
        nodes.Add(new HwOperationNode("result_bits", "bitcast", ["result_signed64"], u64, Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_result", "result", "result_bits", origin));
        return new HwModule("scalar_" + opcodeName.ToLowerInvariant(), "scalar_" + opcodeName.ToLowerInvariant(), ports, nodes, [],
            new HwHardwareContract("scalar." + opcodeName.ToLowerInvariant() + ".source-bounded.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0,1)), origin);
    }

    private static HwModule BuildWideDivRemModule(string opcodeName, string target, HwSourceOrigin origin)
    {
        var u1 = new HwBitVectorType(1); var u64 = new HwBitVectorType(64);
        HwPortContract[] ports = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
        var nodes = new List<HwNode>();
        void C(string id, ulong value, HwBitVectorType? type = null) => nodes.Add(new HwConstantNode(id, type ?? u64, Convert.ToString(unchecked((long)value), 2).PadLeft((type ?? u64).Width, '0'), origin));
        void O(string id, string op, string a, string b, HwBitVectorType? type = null) => nodes.Add(new HwOperationNode(id, op, [a, b], type ?? u64, Origin: origin));
        C("zero", 0); C("one", 1); C("one_bit", 1, u1); C("all_ones", ulong.MaxValue); C("sixty_three", 63);
        string remainder = "zero", quotient = "zero";
        // 64-step unsigned restoring division. The carry bit from rem<<1 is retained
        // separately, so the trial remainder is mathematically 65-bit without adding a
        // wider-than-64 HWIR operation. When carry is set, subtraction is mandatory.
        for (int i = 63; i >= 0; i--)
        {
            C("shift_amount_" + i, (ulong)i);
            O("dividend_shift_" + i, "lshr64_mask6", "op1", "shift_amount_" + i);
            O("dividend_bit_" + i, "and", "dividend_shift_" + i, "one");
            O("rem_carry_" + i, "lshr64_mask6", remainder, "sixty_three");
            O("overflow_" + i, "eq", "rem_carry_" + i, "one", u1);
            O("rem_shift_" + i, "shl64_mask6", remainder, "one");
            O("trial_" + i, "or", "rem_shift_" + i, "dividend_bit_" + i);
            O("trial_less_" + i, "ult64", "trial_" + i, "op2", u1);
            O("not_less_" + i, "xor", "trial_less_" + i, "one_bit", u1);
            O("subtract_" + i, "or", "overflow_" + i, "not_less_" + i, u1);
            O("trial_minus_divisor_" + i, "sub", "trial_" + i, "op2");
            string nextRemainder = "rem_next_" + i;
            nodes.Add(new HwMuxNode(nextRemainder, "subtract_" + i, "trial_minus_divisor_" + i, "trial_" + i, u64, origin));
            O("quotient_shift_" + i, "shl64_mask6", quotient, "one");
            nodes.Add(new HwOperationNode("quotient_bit64_" + i, "zext", ["subtract_" + i], u64, Origin: origin));
            string nextQuotient = "quotient_next_" + i;
            O(nextQuotient, "or", "quotient_shift_" + i, "quotient_bit64_" + i);
            remainder = nextRemainder; quotient = nextQuotient;
        }
        O("divisor_zero", "eq", "op2", "zero", u1);
        string selected = target switch
        {
            "wide:DIV" => "zero",
            "wide:DIVU" => "all_ones",
            "wide:REMU" => "op1",
            _ => throw new InvalidDataException("Unknown wide div/rem target " + target)
        };
        string computed = target == "wide:REMU" ? remainder : quotient;
        nodes.Add(new HwMuxNode("result_value", "divisor_zero", selected, computed, u64, origin));
        nodes.Add(new HwOutputDriveNode("drive_result", "result", "result_value", origin));
        string module = "scalar_" + opcodeName.ToLowerInvariant();
        return new HwModule(module, module, ports, nodes, [], new HwHardwareContract("scalar." + opcodeName.ToLowerInvariant() + ".source-bounded.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildSignedRemainderModule(string opcodeName, HwSourceOrigin origin)
    {
        var u1 = new HwBitVectorType(1); var u8 = new HwBitVectorType(8); var u64 = new HwBitVectorType(64);
        var ports = new HwPortContract[] { new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64), new("fatal_valid", HwPortDirection.Output, u1), new("fatal_code", HwPortDirection.Output, u8) };
        var nodes = new List<HwNode>();
        void C(string id, ulong value, HwBitVectorType? type = null) => nodes.Add(new HwConstantNode(id, type ?? u64, Convert.ToString(unchecked((long)value), 2).PadLeft((type ?? u64).Width, '0'), origin));
        void O(string id, string op, string a, string b, HwBitVectorType? type = null) => nodes.Add(new HwOperationNode(id, op, [a, b], type ?? u64, Origin: origin));
        C("zero", 0); C("one", 1); C("one_bit", 1, u1); C("fatal_seven", 7, u8); C("dividend_min", 0x8000000000000000); C("divisor_minus_one", ulong.MaxValue);
        O("fatal_a", "eq", "op1", "dividend_min", u1); O("fatal_b", "eq", "op2", "divisor_minus_one", u1); O("fatal_pair", "and", "fatal_a", "fatal_b", u1);
        O("a_sign", "lshr64_mask6", "op1", "sixty_three"); O("b_sign", "lshr64_mask6", "op2", "sixty_three");
        C("sixty_three", 63); nodes.Add(new HwOperationNode("a_negative", "trunc", ["a_sign"], u1, Origin: origin)); nodes.Add(new HwOperationNode("b_negative", "trunc", ["b_sign"], u1, Origin: origin));
        O("a_not", "xor", "op1", "all_ones"); O("b_not", "xor", "op2", "all_ones"); C("all_ones", ulong.MaxValue);
        O("a_negated", "add", "a_not", "one"); O("b_negated", "add", "b_not", "one");
        nodes.Add(new HwMuxNode("a_magnitude", "a_negative", "a_negated", "op1", u64, origin)); nodes.Add(new HwMuxNode("b_magnitude", "b_negative", "b_negated", "op2", u64, origin));
        string rem = "zero";
        for (int i = 63; i >= 0; i--)
        {
            C("shift_" + i, (ulong)i); O("bit_shift_" + i, "lshr64_mask6", "a_magnitude", "shift_" + i); O("bit_" + i, "and", "bit_shift_" + i, "one");
            O("carry_" + i, "lshr64_mask6", rem, "sixty_three"); O("carry_set_" + i, "eq", "carry_" + i, "one", u1);
            O("shifted_" + i, "shl64_mask6", rem, "one"); O("trial_" + i, "or", "shifted_" + i, "bit_" + i);
            O("less_" + i, "ult64", "trial_" + i, "b_magnitude", u1); O("not_less_" + i, "xor", "less_" + i, "one_bit", u1); O("subtract_" + i, "or", "carry_set_" + i, "not_less_" + i, u1);
            O("difference_" + i, "sub", "trial_" + i, "b_magnitude"); string next = "rem_next_" + i; nodes.Add(new HwMuxNode(next, "subtract_" + i, "difference_" + i, "trial_" + i, u64, origin)); rem = next;
        }
        O("neg_rem_bits", "xor", rem, "all_ones"); O("neg_rem", "add", "neg_rem_bits", "one"); nodes.Add(new HwMuxNode("signed_rem", "a_negative", "neg_rem", rem, u64, origin));
        O("divisor_zero", "eq", "op2", "zero", u1); nodes.Add(new HwMuxNode("zero_divisor_result", "divisor_zero", "op1", "signed_rem", u64, origin));
        nodes.Add(new HwMuxNode("result_value", "fatal_pair", "zero", "zero_divisor_result", u64, origin));
        nodes.Add(new HwOutputDriveNode("drive_result", "result", "result_value", origin)); nodes.Add(new HwOutputDriveNode("drive_fatal_valid", "fatal_valid", "fatal_pair", origin));
        C("fatal_zero", 0, u8); nodes.Add(new HwMuxNode("fatal_code_value", "fatal_pair", "fatal_seven", "fatal_zero", u8, origin)); nodes.Add(new HwOutputDriveNode("drive_fatal_code", "fatal_code", "fatal_code_value", origin));
        string module = "scalar_" + opcodeName.ToLowerInvariant();
        return new HwModule(module, module, ports, nodes, [], new HwHardwareContract("scalar.rem.signed-fatal-sideband.candidate.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildCarrylessModule(string opcodeName, string target, HwSourceOrigin origin)
    {
        var u1 = new HwBitVectorType(1); var u64 = new HwBitVectorType(64);
        var ports = new[] { new HwPortContract("op1", HwPortDirection.Input, u64), new HwPortContract("op2", HwPortDirection.Input, u64), new HwPortContract("result", HwPortDirection.Output, u64) };
        var nodes = new List<HwNode>();
        void C(string id, ulong value) => nodes.Add(new HwConstantNode(id, u64, Convert.ToString(unchecked((long)value), 2).PadLeft(64, '0'), origin));
        void O(string id, string op, string a, string? b = null, HwBitVectorType? type = null) => nodes.Add(new HwOperationNode(id, op, b is null or "" ? [a] : [a, b], type ?? u64, Origin: origin));
        C("zero", 0); C("one", 1); C("all_ones", ulong.MaxValue); C("one_bit", 1); C("sixty_three", 63);
        string low = "op1", high = "zero";
        string accLow = "zero", accHigh = "zero";
        for (int bit = 0; bit < 64; bit++)
        {
            C("shift_amount_" + bit, (ulong)bit);
            O("multiplier_shift_" + bit, "lshr64_mask6", "op2", "shift_amount_" + bit);
            O("multiplier_bit_" + bit, "and", "multiplier_shift_" + bit, "one");
            O("select_mask_" + bit, "sub", "zero", "multiplier_bit_" + bit);
            O("selected_low_" + bit, "and", low, "select_mask_" + bit);
            O("selected_high_" + bit, "and", high, "select_mask_" + bit);
            O("acc_low_" + bit, "xor", accLow, "selected_low_" + bit);
            O("acc_high_" + bit, "xor", accHigh, "selected_high_" + bit);
            O("next_low_" + bit, "shl64_mask6", low, "one");
            O("carry_bit_" + bit, "lshr64_mask6", low, "sixty_three");
            O("next_high_shift_" + bit, "shl64_mask6", high, "one");
            O("next_high_" + bit, "or", "next_high_shift_" + bit, "carry_bit_" + bit);
            low = "next_low_" + bit; high = "next_high_" + bit;
            accLow = "acc_low_" + bit; accHigh = "acc_high_" + bit;
        }
        string result;
        if (target == "CarryLessMultiplyLow64") result = accLow;
        else if (target == "CarryLessMultiplyHigh64") result = accHigh;
        else if (target == "CarryLessMultiplyReverse64")
        {
            O("reverse_low_window", "lshr64_mask6", accLow, "sixty_three");
            O("reverse_high_window", "shl64_mask6", accHigh, "one");
            O("reverse_window", "or", "reverse_low_window", "reverse_high_window");
            result = "reverse_window";
        }
        else throw new InvalidDataException("Unknown carryless method " + target);
        nodes.Add(new HwOutputDriveNode("drive_result", "result", result, origin));
        string module = "scalar_" + opcodeName.ToLowerInvariant();
        return new HwModule(module, module, ports, nodes, [], new HwHardwareContract("scalar." + opcodeName.ToLowerInvariant() + ".source-bounded.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildHighMultiplyModule(string opcodeName, string target, HwSourceOrigin origin)
    {
        var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64); var u1 = new HwBitVectorType(1);
        HwPortContract[] ports = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
        var nodes = new List<HwNode>();
        void C(string id, ulong value) => nodes.Add(new HwConstantNode(id, u64, Convert.ToString(unchecked((long)value), 2).PadLeft(64, '0'), origin));
        void O(string id, string op, string a, string? b = null, HwBitVectorType? type = null) => nodes.Add(new HwOperationNode(id, op, b is null or "" ? [a] : [a, b], type ?? u64, Origin: origin));
        C("zero", 0); C("one", 1); C("thirty_two", 32); C("sixty_three", 63);
        O("a_low32", "trunc", "op1", "", u32); O("b_low32", "trunc", "op2", "", u32);
        O("a_high_shift", "lshr64_mask6", "op1", "thirty_two"); O("b_high_shift", "lshr64_mask6", "op2", "thirty_two");
        O("a_high32", "trunc", "a_high_shift", "", u32); O("b_high32", "trunc", "b_high_shift", "", u32);
        O("a0", "zext", "a_low32", "", u64); O("a1", "zext", "a_high32", "", u64);
        O("b0", "zext", "b_low32", "", u64); O("b1", "zext", "b_high32", "", u64);
        O("p00", "mul", "a0", "b0"); O("p01", "mul", "a0", "b1"); O("p10", "mul", "a1", "b0"); O("p11", "mul", "a1", "b1");
        O("p00_hi_shift", "lshr64_mask6", "p00", "thirty_two"); O("p01_hi_shift", "lshr64_mask6", "p01", "thirty_two"); O("p10_hi_shift", "lshr64_mask6", "p10", "thirty_two");
        O("p00_hi32", "trunc", "p00_hi_shift", "", u32); O("p01_hi32", "trunc", "p01_hi_shift", "", u32); O("p10_hi32", "trunc", "p10_hi_shift", "", u32);
        O("p01_lo32", "trunc", "p01", "", u32); O("p10_lo32", "trunc", "p10", "", u32);
        O("p00_hi", "zext", "p00_hi32", "", u64); O("p01_hi", "zext", "p01_hi32", "", u64); O("p10_hi", "zext", "p10_hi32", "", u64);
        O("p01_lo", "zext", "p01_lo32", "", u64); O("p10_lo", "zext", "p10_lo32", "", u64);
        O("mid_a", "add", "p00_hi", "p01_lo"); O("mid", "add", "mid_a", "p10_lo");
        O("carry_shift", "lshr64_mask6", "mid", "thirty_two"); O("carry32", "trunc", "carry_shift", "", u32); O("carry", "zext", "carry32", "", u64);
        O("high_a", "add", "p11", "p01_hi"); O("high_b", "add", "high_a", "p10_hi"); O("unsigned_high", "add", "high_b", "carry");
        string result = "unsigned_high";
        if (target is "highmul:MULH" or "highmul:MULHSU")
        {
            O("sign_a", "lshr64_mask6", "op1", "sixty_three");
            O("sign_mask_a", "sub", "zero", "sign_a"); O("correction_a", "and", "sign_mask_a", "op2");
            O("signed_high_a", "sub", "unsigned_high", "correction_a"); result = "signed_high_a";
            if (target == "highmul:MULH")
            {
                O("sign_b", "lshr64_mask6", "op2", "sixty_three");
                O("sign_mask_b", "sub", "zero", "sign_b"); O("correction_b", "and", "sign_mask_b", "op1");
                O("signed_high_b", "sub", "signed_high_a", "correction_b"); result = "signed_high_b";
            }
        }
        else if (target != "highmul:MULHU") throw new InvalidDataException("Unknown high multiply method " + target);
        nodes.Add(new HwOutputDriveNode("drive_result", "result", result, origin));
        string module = "scalar_" + opcodeName.ToLowerInvariant();
        return new HwModule(module, module, ports, nodes, [], new HwHardwareContract("scalar." + opcodeName.ToLowerInvariant() + ".source-bounded.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
    }
}
#endif
