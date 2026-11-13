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
        string baseCommit, HardwareBindingEntry addBinding, HardwareBindingsToolchain toolchain, bool includeDirect = false, bool includeShifts = false, bool includeExtensions = false, bool includeNarrow = false, bool includeAddress = false, bool includeLogic = false, bool includeImmediate = false)
    {
        if ((includeShifts && !includeDirect) || (includeExtensions && !includeShifts) || (includeNarrow && !includeExtensions) || (includeAddress && !includeNarrow) || (includeLogic && !includeAddress) || (includeImmediate && !includeLogic))
            throw new InvalidDataException("Dispatch modes must include all prerequisite modes.");
        var bridge = new BridgeHwirAddWordCompiler();
        var observation = includeDirect ? ScalarWordDispatchReader.Read(alu, opcodeEnum, aluSha, enumSha, true, includeShifts, includeExtensions, includeNarrow, includeAddress, includeLogic, includeImmediate) :
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
        if (includeImmediate) ports = [.. ports, new("immediate", HwPortDirection.Input, u64)];
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
        var shiftModules = new List<HwModule>();
        static bool IsExtension(string method) => method is "SignExtendWordFromScalar" or "ZeroExtendWordFromScalar" or "SignExtendByteFromScalar" or "SignExtendHalfFromScalar" or "ZeroExtendHalfFromScalar";
        foreach (var arm in observation.Arms)
        {
            var direct = arm.TargetMethod.StartsWith("direct:", StringComparison.Ordinal);
            var isShift = shifts.TryGetValue(arm.TargetMethod, out var shiftOp);
            var isExtension = IsExtension(arm.TargetMethod); var isAddress = addressMethods.Contains(arm.TargetMethod); var isLogic = logicMethods.Contains(arm.TargetMethod); var isBit = bitMethods.Contains(arm.TargetMethod); var isCompare = compareMethods.Contains(arm.TargetMethod); var isImmediate = arm.TargetMethod == "ShiftLeftUnsignedWordImmediate";
            var id = direct ? "direct_" + arm.TargetMethod[7..] : isShift ? "shift_" + arm.EnumMember.ToLowerInvariant() : isExtension ? "extension_" + arm.EnumMember.ToLowerInvariant() : isAddress ? "address_" + arm.EnumMember.ToLowerInvariant() : isLogic ? "logic_" + arm.EnumMember.ToLowerInvariant() : isBit ? "bit_" + arm.EnumMember.ToLowerInvariant() : isCompare ? "compare_" + arm.EnumMember.ToLowerInvariant() : isImmediate ? "immediate_slli_uw" : ids[arm.TargetMethod];
            nodes.Add(new HwConstantNode(id + "_opcode", u16, Convert.ToString(arm.Opcode, 2).PadLeft(16, '0'), origin));
            nodes.Add(new HwOperationNode(id + "_match", "eq", ["opcode16", id + "_opcode"], u1, Origin: origin));
            if (direct) nodes.Add(new HwOperationNode(id + "_value", arm.TargetMethod[7..], ["op1", "op2"], u64, Origin: origin));
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
            else if (isCompare)
            {
                var compareOrigin = origin with { Member = arm.TargetMethod, IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("compare-observation/v1|" + aluSha + "|" + arm.TargetMethod))).ToLowerInvariant(), Line = arm.MethodLine, Column = arm.MethodColumn };
                HwPortContract[] leafPorts = [new("op1", HwPortDirection.Input, u64), new("op2", HwPortDirection.Input, u64), new("result", HwPortDirection.Output, u64)];
                var isMin = arm.EnumMember.StartsWith("MIN", StringComparison.Ordinal);
                var signed = !arm.EnumMember.EndsWith("U", StringComparison.Ordinal);
                HwNode[] compareNodes = [new HwOperationNode("less", signed ? "slt64" : "ult64", isMin ? ["op2", "op1"] : ["op1", "op2"], u1, Origin: compareOrigin),
                    new HwMuxNode("value", "less", "op2", "op1", u64, compareOrigin), new HwOutputDriveNode("drive", "result", "value", compareOrigin)];
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
        foreach (var arm in observation.Arms.Where(a => a.TargetMethod.StartsWith("direct:", StringComparison.Ordinal) || shifts.ContainsKey(a.TargetMethod) || IsExtension(a.TargetMethod) || addressMethods.Contains(a.TargetMethod) || logicMethods.Contains(a.TargetMethod) || bitMethods.Contains(a.TargetMethod) || compareMethods.Contains(a.TargetMethod) || a.TargetMethod == "ShiftLeftUnsignedWordImmediate"))
        {
            var id = shifts.ContainsKey(arm.TargetMethod) ? "shift_" + arm.EnumMember.ToLowerInvariant() : IsExtension(arm.TargetMethod) ? "extension_" + arm.EnumMember.ToLowerInvariant() : addressMethods.Contains(arm.TargetMethod) ? "address_" + arm.EnumMember.ToLowerInvariant() : logicMethods.Contains(arm.TargetMethod) ? "logic_" + arm.EnumMember.ToLowerInvariant() : bitMethods.Contains(arm.TargetMethod) ? "bit_" + arm.EnumMember.ToLowerInvariant() : compareMethods.Contains(arm.TargetMethod) ? "compare_" + arm.EnumMember.ToLowerInvariant() : arm.TargetMethod == "ShiftLeftUnsignedWordImmediate" ? "immediate_slli_uw" : "direct_" + arm.TargetMethod[7..];
            nodes.Add(new HwMuxNode(id + "_select", id + "_match", id + "_value", resultNode, u64, origin));
            nodes.Add(new HwOperationNode(id + "_supported", "or", [supportedNode, id + "_match"], u1, Origin: origin));
            resultNode = id + "_select"; supportedNode = id + "_supported";
        }
        nodes.Add(new HwOutputDriveNode("drive_result", "result", resultNode, origin));
        nodes.Add(new HwOutputDriveNode("drive_supported", "supported", supportedNode, origin));
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
}
#endif
