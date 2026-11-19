using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HardwareBridge.Intel8080;

public sealed record SourceFile(string Path, string Sha256, long Length);
public sealed record Member(string Name, string Kind, string Signature, int Line, int Column);
public sealed record TypeModel(string Name, string Namespace, string Kind, string File, int Line,
    IReadOnlyList<Member> Members, IReadOnlyList<string> TypeReferences, IReadOnlyList<string> Warnings);
public sealed record Opcode(byte Code, string Method, bool Mapped, string File, int Line);
public sealed record OpcodeIr(byte Code, string Mnemonic, string SourceMethod, string SourceFile, int SourceLine,
    string BindingStatus, string HardwareStatus, IReadOnlyList<string> ManualTasks);
public sealed record ProjectedEquation(string Name, string Operation, int WidthBits, string Formula,
    string SourceSymbol, string SourceFile, int SourceLine);
public sealed record MethodIr(string Method, string File, int Line, string[] Operations, string[] UnclassifiedSyntax);
public sealed record SemanticReference(string File, int Line, string Syntax, string? Symbol, string? TargetFile, int? TargetLine);
public sealed record MemberDependency(string Kind, string SourceMember, string File, int Line, string Syntax,
    string? TargetSymbol, string? TargetFile, int? TargetLine);
public sealed record ImportModel(string Schema, string Disposition, string SourceRoot,
    IReadOnlyList<SourceFile> Files, IReadOnlyList<TypeModel> Types, IReadOnlyList<Opcode> Opcodes,
    IReadOnlyList<SemanticReference> References, IReadOnlyList<MemberDependency> MemberDependencies,
    IReadOnlyList<string> CompilationDiagnostics,
    IReadOnlyList<string> HardwareDecisions, IReadOnlyList<string> UnsupportedFeatures);

public static class Analyzer
{
    public static IReadOnlyList<MethodIr> BuildMethodIr(ImportModel model, IReadOnlyCollection<string> selectedPaths)
    {
        var result=new List<MethodIr>();
        foreach(string path in selectedPaths.Where(p=>Path.GetFileName(p).Equals("Ops.cs",StringComparison.OrdinalIgnoreCase)))
        {
            var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(path),new CSharpParseOptions(LanguageVersion.Preview),path);
            foreach(var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var ops=new List<string>();var other=new List<string>();
                foreach(var node in method.DescendantNodes().Where(n=>n is StatementSyntax or ExpressionSyntax))
                {
                    string? op=node switch
                    {
                        AssignmentExpressionSyntax a => "assign:"+a.Left,
                        InvocationExpressionSyntax i => "call:"+i.Expression,
                        IfStatementSyntax => "branch:if",
                        SwitchStatementSyntax => "branch:switch",
                        ReturnStatementSyntax => "return",
                        PrefixUnaryExpressionSyntax p => "unary:"+p.OperatorToken.ValueText,
                        PostfixUnaryExpressionSyntax p => "unary:"+p.OperatorToken.ValueText,
                        BinaryExpressionSyntax b => "binary:"+b.OperatorToken.ValueText,
                        ElementAccessExpressionSyntax e => "index:"+e.Expression,
                        _ => null
                    };
                    if(op is null)continue;
                    var row=$"{op}@{tree.GetLineSpan(node.Span).StartLinePosition.Line+1}";
                    if(node is StatementSyntax or AssignmentExpressionSyntax or InvocationExpressionSyntax or BinaryExpressionSyntax or ElementAccessExpressionSyntax)ops.Add(row);
                    else other.Add(row);
                }
                int line=tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line+1;
                result.Add(new(method.Identifier.ValueText,path,line,ops.Distinct().ToArray(),other.Distinct().ToArray()));
            }
        }
        return result;
    }

    public static IReadOnlyList<OpcodeIr> BuildOpcodeIr(ImportModel model)
    {
        var opsFile=model.Files.FirstOrDefault(f=>Path.GetFileName(f.Path).Equals("Ops.cs",StringComparison.OrdinalIgnoreCase))?.Path;
        var methods=new Dictionary<string,(string File,int Line)>(StringComparer.Ordinal);
        foreach(var type in model.Types.Where(t=>Path.GetFileName(t.File).Equals("Ops.cs",StringComparison.OrdinalIgnoreCase)))
            foreach(var member in type.Members.Where(m=>m.Kind=="method")) methods.TryAdd(member.Name,(type.File,member.Line));
        return model.Opcodes.Select(op=>
        {
            methods.TryGetValue(op.Method,out var origin);
            return new OpcodeIr(op.Code,$"OP_{op.Code:X2}",op.Method,origin.File??opsFile??op.File,origin.Line,
                op.Mapped&&origin.File is not null?"Roslyn symbol resolved":"unresolved; inspect source",
                "TODO: manual operation semantics and timing; no automatic C# to hardware lowering",
                new[]{"Inspect method data/control effects and helper closure", "Define operand, flag, PC and memory effects", "Add an instruction-level hardware verification vector"});
        }).ToArray();
    }

    // A deliberately narrow, fail-closed source-backed projection for the
    // emulator's ADD helper. This is declarative HWIR evidence, not a generic
    // C# compiler and not sequential CPU lowering.
    public static object BuildAddBehaviorProjection(ImportModel model)
    {
        string opsPath = RequireSource(model, "Ops.cs");
        string flagsPath = RequireSource(model, "Flags.cs");
        string utilsPath = RequireSource(model, "Utils.cs");
        // The checks below are intentionally tied to the exact source snapshot
        // reviewed for this projection. Text fragments alone would not detect
        // added branches, reordering, or extra writes around the same fragments.
        RequirePinnedSourceHash(model, opsPath, "b8ec21f18890df180b9c9cbddb54f6bff48cbf0f47759156ef04463c1414b44a");
        RequirePinnedSourceHash(model, flagsPath, "1074eb1636b4ed44bd55689516ad3cc1b00c7b18f106ef7acfff55d33f7239fd");
        RequirePinnedSourceHash(model, utilsPath, "1f8e84a067973e2629bf33d8ae128984a25ade69bab9c21fec1e0b114caf77b6");
        var opsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(opsPath), new CSharpParseOptions(LanguageVersion.Preview), opsPath);
        var flagsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(flagsPath), new CSharpParseOptions(LanguageVersion.Preview), flagsPath);
        var utilsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(utilsPath), new CSharpParseOptions(LanguageVersion.Preview), utilsPath);

        var add = RequireMethod(opsTree, "ADD", "Memory", "Cpu", "byte");
        RequireFragments(add, "ushort result = (ushort)(cpu.Registers.A + value);",
            "cpu.Registers.Flags.CalcSZPC(result);",
            "cpu.Registers.Flags.CalcAuxCarryFlag(cpu.Registers.A, value);",
            "cpu.Registers.ProgramCounter++;",
            "cpu.Registers.A = (byte)(result & 0xff);",
            "return 4;");

        var expectedDispatch = new (byte Code, string Method, string Register)[]
        {
            (0x80, "ADD_B", "B"), (0x81, "ADD_C", "C"), (0x82, "ADD_D", "D"), (0x87, "ADD_A", "A")
        };
        foreach (var item in expectedDispatch)
        {
            var opcode = model.Opcodes.SingleOrDefault(op => op.Code == item.Code);
            if (opcode is null || opcode.Method != item.Method)
                throw new InvalidDataException($"ADD dispatch mapping changed or is missing: 0x{item.Code:X2} -> {item.Method}.");
            var wrapper = RequireMethod(opsTree, item.Method, "Memory", "Cpu");
            string target = $"ADD(memory, cpu, cpu.Registers.{item.Register})";
            if (wrapper.ExpressionBody?.Expression.ToString() != target)
                throw new InvalidDataException($"ADD wrapper {item.Method} is not the expected source binding: {target}.");
        }

        var szpc = RequireMethod(flagsTree, "CalcSZPC", "ushort");
        RequireFragments(szpc, "this.CalcZeroFlag(val);", "this.CalcSignFlag(val);",
            "this.CalcParityFlag((byte)(val & 0xff));", "this.CalcCarryFlag(val);");
        var zero = RequireMethod(flagsTree, "CalcZeroFlag", "UInt16");
        RequireFragments(zero, "Zero = ((value & 0xff) == 0);");
        var sign = RequireMethod(flagsTree, "CalcSignFlag", "UInt16");
        RequireFragments(sign, "Sign = ((value & SignFlag) == SignFlag);");
        var carry = RequireMethod(flagsTree, "CalcCarryFlag", "UInt16");
        RequireFragments(carry, "Carry = (value > 0xff);");
        var parity = RequireMethod(flagsTree, "CalcParityFlag", "byte");
        RequireFragments(parity, "Parity = Utils.GetParity(result);");
        var aux = RequireMethod(flagsTree, "CalcAuxCarryFlag", "byte", "byte");
        RequireFragments(aux, "AuxCarry = (byte)((a & 0x0f) + (b & 0x0f)) > 0x0f;");
        var getParity = RequireMethod(utilsTree, "GetParity", "byte");
        RequireFragments(getParity, "return 1 == ParityTable[val];");
        VerifyEvenParityTable(utilsTree);

        var equations = new[]
        {
            Equation("sum9", "add", 9, "zero_extend_1(A) + zero_extend_1(operand)", add),
            Equation("result8", "truncate", 8, "sum9[7:0]", add),
            Equation("A_next", "assign", 8, "result8", add),
            Equation("CY_next", "bit", 1, "sum9[8] (source helper: ushort value > 0xFF)", carry),
            Equation("AC_next", "compare", 1, "zero_extend_1(A[3:0]) + zero_extend_1(operand[3:0]) > 0xF", aux),
            Equation("Z_next", "compare", 1, "result8 == 0", zero),
            Equation("S_next", "bit", 1, "result8[7]", sign),
            Equation("P_next", "even_parity", 1, "even_parity8(result8); source verified against all 256 ParityTable entries", getParity),
            Equation("PC_next", "increment", 16, "PC + 1", add)
        };
        var dispatch = expectedDispatch.Select(item => new
        {
            opcode = $"0x{item.Code:X2}", mnemonic = $"ADD {item.Register}", sourceMethod = item.Method,
            sourceFile = opsPath, sourceLine = MethodLine(RequireMethod(opsTree, item.Method, "Memory", "Cpu")),
            operandRegister = item.Register
        }).ToArray();
        var traces = new[] { add, szpc, zero, sign, carry, parity, aux, getParity }
            .Select(method => Span(model, method)).ToArray();

        return new
        {
            schema = "hardwarebridge.intel8080.add-hwir/v1",
            disposition = "source-snapshot-pinned declarative ADD projection with Roslyn text-pattern checks and separately recorded PC effect; no automatic MLIR/SystemVerilog emission",
            sourceHashes = new[] { SourceHash(model, opsPath), SourceHash(model, flagsPath), SourceHash(model, utilsPath) },
            dispatch,
            inputs = new[] { new { name = "A", widthBits = 8 }, new { name = "operand", widthBits = 8 } },
            equations,
            sourceTrace = traces,
            observableEffects = new[] { "A := result8", "S/Z/P/CY/AC := projected equations", "ProgramCounter := ProgramCounter + 1", "source helper returns 4 emulator cycles (not a hardware clock-latency claim)" },
            deltasAndLimits = new[]
            {
                "This projection covers only source-dispatched ADD opcodes 80, 81, 82 and 87; candidate RTL's 80-87 mask expands additional operands not present in Cpu._ops.",
                "The emulator's ushort sum is equivalent to the 9-bit sum because two byte operands sum to at most 510.",
                "The PC effect is represented as an architectural state equation; the emulator's helper call/return cycle count is not mapped to a clock schedule.",
                "CircuitWeave's current scalar CIRCT path accepts one output driver and does not lower multiple flag outputs or sequential state. No MLIR/SystemVerilog is emitted by this projection."
            }
        };
    }

    // Source-pinned ADC projection. Carry-in is an explicit hardware input;
    // the C# source does not provide a generic compiler or cycle schedule.
    public static object BuildAdcBehaviorProjection(ImportModel model)
    {
        string opsPath = RequireSource(model, "Ops.cs");
        string flagsPath = RequireSource(model, "Flags.cs");
        string utilsPath = RequireSource(model, "Utils.cs");
        RequirePinnedSourceHash(model, opsPath, "b8ec21f18890df180b9c9cbddb54f6bff48cbf0f47759156ef04463c1414b44a");
        RequirePinnedSourceHash(model, flagsPath, "1074eb1636b4ed44bd55689516ad3cc1b00c7b18f106ef7acfff55d33f7239fd");
        RequirePinnedSourceHash(model, utilsPath, "1f8e84a067973e2629bf33d8ae128984a25ade69bab9c21fec1e0b114caf77b6");
        var opsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(opsPath), new CSharpParseOptions(LanguageVersion.Preview), opsPath);
        var flagsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(flagsPath), new CSharpParseOptions(LanguageVersion.Preview), flagsPath);
        var utilsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(utilsPath), new CSharpParseOptions(LanguageVersion.Preview), utilsPath);

        var adc = RequireMethod(opsTree, "ADC", "Memory", "Cpu", "byte", "byte");
        RequireFragments(adc, "var result = (ushort)(a + b);", "if (cpu.Registers.Flags.Carry)",
            "result++;", "cpu.Registers.Flags.CalcAuxCarryFlag(a, b, 1);",
            "cpu.Registers.Flags.CalcAuxCarryFlag(a, b);", "cpu.Registers.Flags.CalcSZPC(result);",
            "cpu.Registers.ProgramCounter++;", "return (byte)(result & 0xff);");

        var expectedDispatch = new (byte Code, string Method, string Register, int Cycles)[]
        {
            (0x88, "ADC_B", "B", 4), (0x89, "ADC_C", "C", 4), (0x8b, "ADC_E", "E", 4),
            (0x8c, "ADC_H", "H", 4), (0x8d, "ADC_L", "L", 4), (0x8e, "ADC_M", "M", 7)
        };
        if (model.Opcodes.Any(op => op.Code is 0x8a or 0x8f))
            throw new InvalidDataException("ADC source dispatch changed: the reviewed source snapshot omits opcodes 8A and 8F.");
        foreach (var item in expectedDispatch)
        {
            var opcode = model.Opcodes.SingleOrDefault(op => op.Code == item.Code);
            if (opcode is null || opcode.Method != item.Method)
                throw new InvalidDataException($"ADC dispatch mapping changed or is missing: 0x{item.Code:X2} -> {item.Method}.");
            if (item.Register == "M") continue;
            var wrapper = RequireMethod(opsTree, item.Method, "Memory", "Cpu");
            RequireFragments(wrapper, $"cpu.Registers.A = ADC(memory, cpu, cpu.Registers.A, cpu.Registers.{item.Register});",
                $"return {item.Cycles};");
        }
        var memoryWrapper = RequireMethod(opsTree, "ADC_M", "Memory", "Cpu");
        RequireFragments(memoryWrapper, "var value = memory[cpu.Registers.HL];",
            "cpu.Registers.A = ADC(memory, cpu, cpu.Registers.A, value);", "return 7;");

        var szpc = RequireMethod(flagsTree, "CalcSZPC", "ushort");
        RequireFragments(szpc, "this.CalcZeroFlag(val);", "this.CalcSignFlag(val);",
            "this.CalcParityFlag((byte)(val & 0xff));", "this.CalcCarryFlag(val);");
        var zero = RequireMethod(flagsTree, "CalcZeroFlag", "UInt16");
        var sign = RequireMethod(flagsTree, "CalcSignFlag", "UInt16");
        var carry = RequireMethod(flagsTree, "CalcCarryFlag", "UInt16");
        var parity = RequireMethod(flagsTree, "CalcParityFlag", "byte");
        var aux = RequireMethod(flagsTree, "CalcAuxCarryFlag", "byte", "byte", "byte");
        RequireFragments(zero, "Zero = ((value & 0xff) == 0);");
        RequireFragments(sign, "Sign = ((value & SignFlag) == SignFlag);");
        RequireFragments(carry, "Carry = (value > 0xff);");
        RequireFragments(parity, "Parity = Utils.GetParity(result);");
        RequireFragments(aux, "AuxCarry = (byte)((a & 0x0f) + (b & 0x0f) + (c & 0x0f)) > 0x0f;");
        var getParity = RequireMethod(utilsTree, "GetParity", "byte");
        RequireFragments(getParity, "return 1 == ParityTable[val];");
        VerifyEvenParityTable(utilsTree);

        var equations = new[]
        {
            Equation("sum9", "add3", 9, "zero_extend_1(A) + zero_extend_1(operand) + zero_extend_8(CY_in)", adc),
            Equation("result8", "truncate", 8, "sum9[7:0]", adc),
            Equation("A_next", "assign", 8, "result8", adc),
            Equation("CY_next", "bit", 1, "sum9[8] (source helper: ushort value > 0xFF)", carry),
            Equation("AC_next", "compare", 1, "zero_extend_1(A[3:0]) + zero_extend_1(operand[3:0]) + CY_in > 0xF", aux),
            Equation("Z_next", "compare", 1, "result8 == 0", zero),
            Equation("S_next", "bit", 1, "result8[7]", sign),
            Equation("P_next", "even_parity", 1, "even_parity8(result8); source verified against all 256 ParityTable entries", getParity),
            Equation("PC_next", "increment", 16, "PC + 1", adc)
        };
        var dispatch = expectedDispatch.Select(item => new
        {
            opcode = $"0x{item.Code:X2}", mnemonic = $"ADC {item.Register}", sourceMethod = item.Method,
            sourceFile = opsPath, sourceLine = MethodLine(RequireMethod(opsTree, item.Method, "Memory", "Cpu")),
            operandRegister = item.Register, sourceCycles = item.Cycles
        }).ToArray();
        var traces = new[] { adc, memoryWrapper, szpc, zero, sign, carry, parity, aux, getParity,
            RequireMethod(opsTree, "ADC_B", "Memory", "Cpu"), RequireMethod(opsTree, "ADC_C", "Memory", "Cpu"),
            RequireMethod(opsTree, "ADC_E", "Memory", "Cpu"), RequireMethod(opsTree, "ADC_H", "Memory", "Cpu"),
            RequireMethod(opsTree, "ADC_L", "Memory", "Cpu") }.Select(method => Span(model, method)).ToArray();

        return new
        {
            schema = "hardwarebridge.intel8080.adc-hwir/v1",
            disposition = "source-snapshot-pinned declarative ADC projection with explicit carry-in, Roslyn text-pattern checks, and separately recorded PC effect; no automatic MLIR/SystemVerilog emission",
            sourceHashes = new[] { SourceHash(model, opsPath), SourceHash(model, flagsPath), SourceHash(model, utilsPath) },
            dispatch,
            inputs = new[] { new { name = "A", widthBits = 8 }, new { name = "operand", widthBits = 8 }, new { name = "CY_in", widthBits = 1 } },
            equations,
            sourceTrace = traces,
            observableEffects = new[] { "A := result8", "S/Z/P/CY/AC := projected equations", "ProgramCounter := ProgramCounter + 1", "source wrappers return 4 cycles for register operands or 7 for M (not hardware latency)" },
            deltasAndLimits = new[]
            {
                "Projection covers only source-dispatched ADC opcodes 88, 89, 8B, 8C, 8D, 8E; source omits 8A (D) and 8F (A), while candidate RTL accepts the architectural family.",
                "ADC M reads memory at HL; this projection treats the resolved operand as an input and does not model memory timing.",
                "The ushort sum is equivalent to the 9-bit sum because byte operands plus carry-in are at most 511.",
                "CircuitWeave scalar CIRCT can lower only the sum output here; flags, PC, memory, decode and sequential state are outside this projection."
            }
        };
    }

    // Preserve emulator SBB observables separately from the Intel-selected
    // RTL auxiliary-carry rule; the two intentionally differ for borrow-in.
    public static object BuildSbbBehaviorProjection(ImportModel model)
    {
        string opsPath = RequireSource(model, "Ops.cs");
        string flagsPath = RequireSource(model, "Flags.cs");
        string utilsPath = RequireSource(model, "Utils.cs");
        RequirePinnedSourceHash(model, opsPath, "b8ec21f18890df180b9c9cbddb54f6bff48cbf0f47759156ef04463c1414b44a");
        RequirePinnedSourceHash(model, flagsPath, "1074eb1636b4ed44bd55689516ad3cc1b00c7b18f106ef7acfff55d33f7239fd");
        RequirePinnedSourceHash(model, utilsPath, "1f8e84a067973e2629bf33d8ae128984a25ade69bab9c21fec1e0b114caf77b6");
        var opsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(opsPath), new CSharpParseOptions(LanguageVersion.Preview), opsPath);
        var flagsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(flagsPath), new CSharpParseOptions(LanguageVersion.Preview), flagsPath);
        var utilsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(utilsPath), new CSharpParseOptions(LanguageVersion.Preview), utilsPath);

        var sbb = RequireMethod(opsTree, "SBB", "Memory", "Cpu", "byte", "byte");
        RequireFragments(sbb, "ushort result = (ushort)(a - b - (cpu.Registers.Flags.Carry ? 1 : 0));",
            "cpu.Registers.Flags.CalcSZPC(result);",
            "cpu.Registers.Flags.CalcAuxCarryFlag(a, (byte)(~b & 0xff), 1);",
            "cpu.Registers.ProgramCounter++;", "return (byte)(result & 0xff);");
        var expectedDispatch = new (byte Code, string Method, string Register, int Cycles)[]
        {
            (0x99, "SBB_C", "C", 4), (0x9a, "SBB_D", "D", 4), (0x9b, "SBB_E", "E", 4),
            (0x9c, "SBB_H", "H", 4), (0x9d, "SBB_L", "L", 4), (0x9e, "SBB_M", "M", 7),
            (0x9f, "SBB_A", "A", 4)
        };
        if (model.Opcodes.Any(op => op.Code == 0x98))
            throw new InvalidDataException("SBB source dispatch changed: reviewed snapshot omits opcode 98 (SBB B).");
        foreach (var item in expectedDispatch)
        {
            var opcode = model.Opcodes.SingleOrDefault(op => op.Code == item.Code);
            if (opcode is null || opcode.Method != item.Method)
                throw new InvalidDataException($"SBB dispatch mapping changed or is missing: 0x{item.Code:X2} -> {item.Method}.");
            if (item.Register == "M") continue;
            var wrapper = RequireMethod(opsTree, item.Method, "Memory", "Cpu");
            RequireFragments(wrapper, $"cpu.Registers.A = SBB(memory, cpu, cpu.Registers.A, cpu.Registers.{item.Register});",
                $"return {item.Cycles};");
        }
        var memoryWrapper = RequireMethod(opsTree, "SBB_M", "Memory", "Cpu");
        RequireFragments(memoryWrapper, "var mem = memory[cpu.Registers.HL];",
            "cpu.Registers.A = SBB(memory, cpu, cpu.Registers.A, mem);", "return 7;");

        var szpc = RequireMethod(flagsTree, "CalcSZPC", "ushort");
        var zero = RequireMethod(flagsTree, "CalcZeroFlag", "UInt16");
        var sign = RequireMethod(flagsTree, "CalcSignFlag", "UInt16");
        var carry = RequireMethod(flagsTree, "CalcCarryFlag", "UInt16");
        var parity = RequireMethod(flagsTree, "CalcParityFlag", "byte");
        var aux = RequireMethod(flagsTree, "CalcAuxCarryFlag", "byte", "byte", "byte");
        RequireFragments(szpc, "this.CalcZeroFlag(val);", "this.CalcSignFlag(val);",
            "this.CalcParityFlag((byte)(val & 0xff));", "this.CalcCarryFlag(val);");
        RequireFragments(zero, "Zero = ((value & 0xff) == 0);");
        RequireFragments(sign, "Sign = ((value & SignFlag) == SignFlag);");
        RequireFragments(carry, "Carry = (value > 0xff);");
        RequireFragments(parity, "Parity = Utils.GetParity(result);");
        RequireFragments(aux, "AuxCarry = (byte)((a & 0x0f) + (b & 0x0f) + (c & 0x0f)) > 0x0f;");
        var getParity = RequireMethod(utilsTree, "GetParity", "byte");
        RequireFragments(getParity, "return 1 == ParityTable[val];");
        VerifyEvenParityTable(utilsTree);

        var equations = new[]
        {
            Equation("result16", "subtract3_wrap16", 16, "(A - operand - CY_in) mod 65536", sbb),
            Equation("result8", "truncate", 8, "result16[7:0]", sbb),
            Equation("A_next", "assign", 8, "result8", sbb),
            Equation("CY_next", "compare", 1, "result16 > 0x00FF (source helper; borrow on byte underflow)", carry),
            Equation("AC_source_next", "compare", 1, "zero_extend_1(A[3:0]) + zero_extend_1((~operand)[3:0]) + 1 > 0xF (source omits CY_in)", aux),
            Equation("Z_next", "compare", 1, "result8 == 0", zero),
            Equation("S_next", "bit", 1, "result8[7]", sign),
            Equation("P_next", "even_parity", 1, "even_parity8(result8); source verified against all 256 ParityTable entries", getParity),
            Equation("PC_next", "increment", 16, "PC + 1", sbb)
        };
        var dispatch = expectedDispatch.Select(item => new
        {
            opcode = $"0x{item.Code:X2}", mnemonic = $"SBB {item.Register}", sourceMethod = item.Method,
            sourceFile = opsPath, sourceLine = MethodLine(RequireMethod(opsTree, item.Method, "Memory", "Cpu")),
            operandRegister = item.Register, sourceCycles = item.Cycles
        }).ToArray();
        var traces = new[] { sbb, memoryWrapper, szpc, zero, sign, carry, parity, aux, getParity,
            RequireMethod(opsTree, "SBB_C", "Memory", "Cpu"), RequireMethod(opsTree, "SBB_D", "Memory", "Cpu"),
            RequireMethod(opsTree, "SBB_E", "Memory", "Cpu"), RequireMethod(opsTree, "SBB_H", "Memory", "Cpu"),
            RequireMethod(opsTree, "SBB_L", "Memory", "Cpu"), RequireMethod(opsTree, "SBB_A", "Memory", "Cpu")
        }.Select(method => Span(model, method)).ToArray();

        return new
        {
            schema = "hardwarebridge.intel8080.sbb-hwir/v1",
            disposition = "source-snapshot-pinned SBB source-observable projection with explicit CY_in; source AC and spec-selected RTL AC are separate equations",
            sourceHashes = new[] { SourceHash(model, opsPath), SourceHash(model, flagsPath), SourceHash(model, utilsPath) },
            dispatch,
            inputs = new[] { new { name = "A", widthBits = 8 }, new { name = "operand", widthBits = 8 }, new { name = "CY_in", widthBits = 1 } },
            equations,
            architectureSelection = new
            {
                auxCarryName = "AC_spec_next",
                formula = "zero_extend_1(A[3:0]) + zero_extend_1((~operand)[3:0]) + (1 - CY_in) > 0xF",
                decision = "Intel 8080 SBB folds CY into the subtrahend and derives auxiliary carry from bit-A3 carry-out of two's-complement subtraction; candidate RTL uses this spec equation.",
                sourceDelta = "C# SBB calls CalcAuxCarryFlag(A, ~operand, 1) without CY_in. When CY_in=1 and low nibbles match, source AC is 1 while spec/RTL AC is 0."
            },
            sourceTrace = traces,
            observableEffects = new[] { "A := result8", "S/Z/P/CY := source equations", "PC := PC + 1", "source AC := AC_source_next", "register wrappers return 4 emulator cycles; M returns 7 (not hardware latency)" },
            deltasAndLimits = new[]
            {
                "Projection covers source-dispatched SBB opcodes 99-9F; 98h SBB B is absent, while candidate RTL expands the family.",
                "Source AC ignores incoming CY; candidate RTL deliberately follows the Intel SBB bit-A3 carry-out rule instead.",
                "SBB M reads memory at HL; this projection treats the resolved operand as an input and does not model memory timing.",
                "CircuitWeave scalar output can represent the wrapped 16-bit subtraction result only; flag output and sequencing are separate."
            }
        };
    }

    // SUB_B and SUI have distinct source handlers; SUI leaves AC stale while
    // the architecture-selected RTL updates it from the subtraction result.
    public static object BuildSubBehaviorProjection(ImportModel model)
    {
        string opsPath = RequireSource(model, "Ops.cs");
        string flagsPath = RequireSource(model, "Flags.cs");
        string utilsPath = RequireSource(model, "Utils.cs");
        RequirePinnedSourceHash(model, opsPath, "b8ec21f18890df180b9c9cbddb54f6bff48cbf0f47759156ef04463c1414b44a");
        RequirePinnedSourceHash(model, flagsPath, "1074eb1636b4ed44bd55689516ad3cc1b00c7b18f106ef7acfff55d33f7239fd");
        RequirePinnedSourceHash(model, utilsPath, "1f8e84a067973e2629bf33d8ae128984a25ade69bab9c21fec1e0b114caf77b6");
        var opsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(opsPath), new CSharpParseOptions(LanguageVersion.Preview), opsPath);
        var flagsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(flagsPath), new CSharpParseOptions(LanguageVersion.Preview), flagsPath);
        var utilsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(utilsPath), new CSharpParseOptions(LanguageVersion.Preview), utilsPath);

        var sub = RequireMethod(opsTree, "SUB", "Memory", "Cpu", "byte");
        RequireFragments(sub, "ushort result = (ushort)(cpu.Registers.A - value);",
            "cpu.Registers.Flags.CalcSZPC(result);",
            "cpu.Registers.Flags.CalcAuxCarryFlag(cpu.Registers.A, (byte)(~value & 0xff), 1);",
            "cpu.Registers.A = (byte)(result & 0xff);", "cpu.Registers.ProgramCounter++;", "return 4;");
        var subB = RequireMethod(opsTree, "SUB_B", "Memory", "Cpu");
        if (subB.ExpressionBody?.Expression.ToString() != "SUB(memory, cpu, cpu.Registers.B)")
            throw new InvalidDataException("SUB_B wrapper no longer binds to the reviewed SUB helper.");
        var sui = RequireMethod(opsTree, "SUI", "Memory", "Cpu");
        RequireFragments(sui, "byte data = memory[cpu.Registers.ProgramCounter + 1];",
            "ushort diff = (ushort)(cpu.Registers.A - data);",
            "cpu.Registers.A = (byte)(diff & 0xFF);", "cpu.Registers.Flags.CalcSZPC(diff);",
            "cpu.Registers.ProgramCounter += 2;", "return 7;");

        foreach (var item in new[] { (Code: (byte)0x90, Method: "SUB_B"), (Code: (byte)0xd6, Method: "SUI") })
        {
            var opcode = model.Opcodes.SingleOrDefault(op => op.Code == item.Code);
            if (opcode is null || opcode.Method != item.Method)
                throw new InvalidDataException($"SUB source dispatch changed: 0x{item.Code:X2} -> {item.Method}.");
        }
        if (model.Opcodes.Any(op => op.Code is >= 0x91 and <= 0x97))
            throw new InvalidDataException("SUB source dispatch changed: reviewed snapshot omits 91h-97h register forms.");

        var szpc = RequireMethod(flagsTree, "CalcSZPC", "ushort");
        var zero = RequireMethod(flagsTree, "CalcZeroFlag", "UInt16");
        var sign = RequireMethod(flagsTree, "CalcSignFlag", "UInt16");
        var carry = RequireMethod(flagsTree, "CalcCarryFlag", "UInt16");
        var parity = RequireMethod(flagsTree, "CalcParityFlag", "byte");
        var aux = RequireMethod(flagsTree, "CalcAuxCarryFlag", "byte", "byte", "byte");
        RequireFragments(szpc, "this.CalcZeroFlag(val);", "this.CalcSignFlag(val);",
            "this.CalcParityFlag((byte)(val & 0xff));", "this.CalcCarryFlag(val);");
        RequireFragments(zero, "Zero = ((value & 0xff) == 0);");
        RequireFragments(sign, "Sign = ((value & SignFlag) == SignFlag);");
        RequireFragments(carry, "Carry = (value > 0xff);");
        RequireFragments(parity, "Parity = Utils.GetParity(result);");
        RequireFragments(aux, "AuxCarry = (byte)((a & 0x0f) + (b & 0x0f) + (c & 0x0f)) > 0x0f;");
        var getParity = RequireMethod(utilsTree, "GetParity", "byte");
        RequireFragments(getParity, "return 1 == ParityTable[val];");
        VerifyEvenParityTable(utilsTree);

        var equations = new[]
        {
            Equation("result16", "subtract_wrap16", 16, "(A - operand) mod 65536", sub),
            Equation("result8", "truncate", 8, "result16[7:0]", sub),
            Equation("A_next", "assign", 8, "result8", sub),
            Equation("CY_next", "compare", 1, "result16 > 0x00FF (source helper; borrow on byte underflow)", carry),
            Equation("AC_SUB_B_source_next", "compare", 1, "zero_extend_1(A[3:0]) + zero_extend_1((~operand)[3:0]) + 1 > 0xF", aux),
            Equation("AC_SUI_source_effect", "preserve", 1, "AC_prev (SUI source handler does not write AC)", sui),
            Equation("Z_next", "compare", 1, "result8 == 0", zero),
            Equation("S_next", "bit", 1, "result8[7]", sign),
            Equation("P_next", "even_parity", 1, "even_parity8(result8); source verified against all 256 ParityTable entries", getParity),
            Equation("PC_SUB_B_next", "increment", 16, "PC + 1", sub),
            Equation("PC_SUI_next", "increment", 16, "PC + 2", sui)
        };
        var traces = new[] { sub, subB, sui, szpc, zero, sign, carry, parity, aux, getParity }
            .Select(method => Span(model, method)).ToArray();

        return new
        {
            schema = "hardwarebridge.intel8080.sub-hwir/v1",
            disposition = "source-snapshot-pinned SUB projection for SUB_B and SUI; source AC effects and architecture-selected RTL AC are separate",
            sourceHashes = new[] { SourceHash(model, opsPath), SourceHash(model, flagsPath), SourceHash(model, utilsPath) },
            dispatch = new[]
            {
                new { opcode = "0x90", mnemonic = "SUB B", sourceMethod = "SUB_B", sourceFile = opsPath, sourceLine = MethodLine(subB), sourceCycles = 4 },
                new { opcode = "0xD6", mnemonic = "SUI data", sourceMethod = "SUI", sourceFile = opsPath, sourceLine = MethodLine(sui), sourceCycles = 7 }
            },
            inputs = new[] { new { name = "A", widthBits = 8 }, new { name = "operand", widthBits = 8 }, new { name = "AC_prev", widthBits = 1 } },
            equations,
            architectureSelection = new
            {
                auxCarryName = "AC_spec_next",
                formula = "zero_extend_1(A[3:0]) + zero_extend_1((~operand)[3:0]) + 1 > 0xF",
                decision = "For 8080 subtraction without borrow-in, AC is the bit-A3 carry of two's-complement subtraction; RTL updates AC for both register and immediate SUB.",
                sourceDelta = "SUB_B source helper computes this AC. SUI source handler does not write AC, so its AC remains stale instead of being recomputed."
            },
            sourceTrace = traces,
            observableEffects = new[] { "A := result8; S/Z/P/CY := source equations", "SUB B increments PC by 1 and returns 4 emulator cycles", "SUI fetches PC+1, increments PC by 2, returns 7 emulator cycles", "SUB B source AC := helper equation; SUI source AC := AC_prev" },
            deltasAndLimits = new[]
            {
                "Only 90h SUB B and D6h SUI are present in C# dispatch; RTL accepts the full architectural SUB register family.",
                "SUI does not update source AC; the architecture-selected RTL recomputes AC from bit-A3 carry-out.",
                "The immediate operand fetch and instruction timing are source effects; the scalar result projection treats operand as already resolved.",
                "CircuitWeave lowers only the wrapped 16-bit subtraction result; flags and CPU sequencing remain outside MLIR."
            }
        };
    }

    // DAD is the source's shared 16-bit HL + pair helper. The scalar equation
    // is separately eligible for CircuitWeave's one-output i17 lowering.
    public static object BuildDadBehaviorProjection(ImportModel model)
    {
        string opsPath = RequireSource(model, "Ops.cs");
        string registersPath = RequireSource(model, "Registers.cs");
        string flagsPath = RequireSource(model, "Flags.cs");
        string cpuPath = RequireSource(model, "Cpu.cs");
        RequirePinnedSourceHash(model, opsPath, "b8ec21f18890df180b9c9cbddb54f6bff48cbf0f47759156ef04463c1414b44a");
        RequirePinnedSourceHash(model, registersPath, "25229be90ff98e669bcef1088e6d77adb69cda0b8388f18df9cd9c4ae236a1f6");
        RequirePinnedSourceHash(model, flagsPath, "1074eb1636b4ed44bd55689516ad3cc1b00c7b18f106ef7acfff55d33f7239fd");
        RequirePinnedSourceHash(model, cpuPath, "27ff0a5a94ff2f04cca5e94811e28c82164813e2a8f9172327b83e3aad540f14");

        var opsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(opsPath), new CSharpParseOptions(LanguageVersion.Preview), opsPath);
        var registersTree = CSharpSyntaxTree.ParseText(File.ReadAllText(registersPath), new CSharpParseOptions(LanguageVersion.Preview), registersPath);
        var flagsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(flagsPath), new CSharpParseOptions(LanguageVersion.Preview), flagsPath);

        var expectedDispatch = new (byte Code, string Method, string Operand)[]
        {
            (0x09, "DAD_B", "BC"), (0x19, "DAD_D", "DE"),
            (0x29, "DAD_H", "HL"), (0x39, "DAD_SP", "StackPointer")
        };
        var wrappers = new List<MethodDeclarationSyntax>();
        foreach (var item in expectedDispatch)
        {
            var opcode = model.Opcodes.SingleOrDefault(op => op.Code == item.Code);
            if (opcode is null || opcode.Method != item.Method)
                throw new InvalidDataException($"DAD dispatch mapping changed or is missing: 0x{item.Code:X2} -> {item.Method}.");
            var wrapper = RequireMethod(opsTree, item.Method, "Memory", "Cpu");
            RequireFragments(wrapper, $"cpu.Registers.DAD(cpu.Registers.{item.Operand});", "return 10;");
            wrappers.Add(wrapper);
        }

        var dad = RequireMethod(registersTree, "DAD", "ushort");
        RequireFragments(dad, "int res = this.HL + value;",
            "this.H = (byte)((res & 0xFF00) >> 8);",
            "this.L = (byte)(res & 0xff);",
            "this.Flags.CalcCarryFlag(res);",
            "this.ProgramCounter++;" );
        if (dad.Body?.Statements.Count != 5)
            throw new InvalidDataException("Registers.DAD body changed; re-audit all state writes before updating this projection.");
        var carry = RequireMethod(flagsTree, "CalcCarryFlag", "int");
        RequireFragments(carry, "Carry = ((result & 0xffff0000) != 0);");

        var equations = new[]
        {
            Equation("sum17", "add", 17, "zero_extend_1(HL) + zero_extend_1(operand)", dad),
            Equation("HL_next", "truncate", 16, "sum17[15:0]", dad),
            Equation("CY_next", "bit", 1, "sum17[16] (source helper: (intResult & 0xFFFF0000) != 0)", carry),
            Equation("S_next", "preserve", 1, "S_prev", dad),
            Equation("Z_next", "preserve", 1, "Z_prev", dad),
            Equation("P_next", "preserve", 1, "P_prev", dad),
            Equation("AC_next", "preserve", 1, "AC_prev", dad),
            Equation("PC_next", "increment", 16, "PC + 1", dad)
        };
        var dispatch = expectedDispatch.Select(item => new
        {
            opcode = $"0x{item.Code:X2}", mnemonic = $"DAD {(item.Operand == "StackPointer" ? "SP" : item.Operand)}",
            sourceMethod = item.Method, sourceFile = opsPath,
            sourceLine = MethodLine(RequireMethod(opsTree, item.Method, "Memory", "Cpu")),
            operandRegister = item.Operand
        }).ToArray();
        var traces = wrappers.Append(dad).Append(carry).Select(method => Span(model, method)).ToArray();
        return new
        {
            schema = "hardwarebridge.intel8080.dad-hwir/v1",
            disposition = "source-snapshot-pinned DAD projection; shared C# helper state effects are checked, while i17 sum is the only CircuitWeave-lowered slice",
            sourceHashes = new[] { SourceHash(model, opsPath), SourceHash(model, registersPath), SourceHash(model, flagsPath), SourceHash(model, cpuPath) },
            dispatch,
            inputs = new[] { new { name = "HL", widthBits = 16 }, new { name = "operand", widthBits = 16 } },
            equations,
            sourceTrace = traces,
            observableEffects = new[] { "HL := sum17[15:0]", "CY := sum17[16]", "S/Z/P/AC unchanged", "PC := PC + 1", "source wrappers return 10 emulator cycles (not a hardware-clock schedule)" },
            architectureSelection = new { flagsOtherThanCY = "preserved", sourceAgreement = "Registers.DAD writes only H/L, CY and PC; source helpers are hash-pinned" },
            deltasAndLimits = new[]
            {
                "The source dispatch contains DAD for BC, DE, HL and SP at 09h, 19h, 29h and 39h.",
                "The 17-bit widened sum covers the maximum mathematical result 0x1FFFE; bit 16 is the carry out.",
                "CircuitWeave lowers only sum17 as one scalar output; flag preservation, PC, opcode selection and CPU sequencing remain outside the lowered module."
            }
        };
    }

    // Logical result and helper side effects are source-pinned. The source's AOR
    // helper actually ANDs; preserve that fact separately from spec-selected OR.
    public static object BuildLogicalBehaviorProjection(ImportModel model)
    {
        string opsPath = RequireSource(model, "Ops.cs");
        string flagsPath = RequireSource(model, "Flags.cs");
        string utilsPath = RequireSource(model, "Utils.cs");
        RequirePinnedSourceHash(model, opsPath, "b8ec21f18890df180b9c9cbddb54f6bff48cbf0f47759156ef04463c1414b44a");
        RequirePinnedSourceHash(model, flagsPath, "1074eb1636b4ed44bd55689516ad3cc1b00c7b18f106ef7acfff55d33f7239fd");
        RequirePinnedSourceHash(model, utilsPath, "1f8e84a067973e2629bf33d8ae128984a25ade69bab9c21fec1e0b114caf77b6");
        var opsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(opsPath), new CSharpParseOptions(LanguageVersion.Preview), opsPath);
        var flagsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(flagsPath), new CSharpParseOptions(LanguageVersion.Preview), flagsPath);
        var utilsTree = CSharpSyntaxTree.ParseText(File.ReadAllText(utilsPath), new CSharpParseOptions(LanguageVersion.Preview), utilsPath);

        var ana = RequireMethod(opsTree, "ANA", "Memory", "Cpu", "byte");
        RequireFragments(ana, "byte result = (byte)(cpu.Registers.A & value);", "cpu.Registers.Flags.Carry = false;",
            "cpu.Registers.Flags.AuxCarry = false;", "cpu.Registers.Flags.CalcZeroFlag(result);",
            "cpu.Registers.Flags.CalcSignFlag(result);", "cpu.Registers.Flags.CalcParityFlag(result);",
            "cpu.Registers.A = (byte)(result & 0xff);", "cpu.Registers.ProgramCounter++;", "return 4;");
        var xor = RequireMethod(opsTree, "XOR", "Memory", "Cpu", "byte");
        RequireFragments(xor, "byte result = (byte)(cpu.Registers.A ^ value);", "cpu.Registers.Flags.CalcSZPC(result);",
            "cpu.Registers.A = (byte)(result & 0xff);", "cpu.Registers.ProgramCounter++;");
        var aor = RequireMethod(opsTree, "AOR", "Memory", "Cpu", "byte");
        RequireFragments(aor, "byte result = (byte)(cpu.Registers.A & value);", "cpu.Registers.Flags.Carry = false;",
            "cpu.Registers.Flags.AuxCarry = false;", "cpu.Registers.A = (byte)(result & 0xff);");
        var oraB = RequireMethod(opsTree, "ORA_B", "Memory", "Cpu");
        RequireFragments(oraB, "cpu.Registers.A | cpu.Registers.B", "cpu.Registers.Flags.CalcSZPC(cpu.Registers.A);",
            "cpu.Registers.Flags.Carry = false;", "cpu.Registers.Flags.AuxCarry = false;");
        var oraM = RequireMethod(opsTree, "ORA_M", "Memory", "Cpu");
        RequireFragments(oraM, "cpu.Registers.A | m", "cpu.Registers.Flags.CalcSZPC(cpu.Registers.A);");
        var ani = RequireMethod(opsTree, "ANI", "Memory", "Cpu");
        RequireFragments(ani, "ANA(memory, cpu, data);", "cpu.Registers.ProgramCounter++;", "return 7;");
        var xri = RequireMethod(opsTree, "XRI", "Memory", "Cpu");
        RequireFragments(xri, "XOR(memory, cpu, data);", "cpu.Registers.ProgramCounter++;", "return 7;");
        var ori = RequireMethod(opsTree, "ORI_D", "Memory", "Cpu");
        RequireFragments(ori, "AOR(memory, cpu, data);", "cpu.Registers.ProgramCounter++;", "return 7;");

        var dispatch = new (byte Code, string Method, string Mnemonic)[]
        {
            (0xa0, "ANA_B", "ANA B"), (0xa7, "ANA_A", "ANA A"), (0xaa, "XRA_D", "XRA D"),
            (0xaf, "XRA_A", "XRA A"), (0xb0, "ORA_B", "ORA B"), (0xb6, "ORA_M", "ORA M"),
            (0xe6, "ANI", "ANI data"), (0xee, "XRI", "XRI data"), (0xf6, "ORI_D", "ORI data")
        };
        foreach (var item in dispatch)
        {
            var sourceOpcode = model.Opcodes.SingleOrDefault(op => op.Code == item.Code);
            if (sourceOpcode is null || sourceOpcode.Method != item.Method)
                throw new InvalidDataException($"Logical dispatch changed: 0x{item.Code:X2} -> {item.Method}.");
        }

        var calcSzpcByte = RequireMethod(flagsTree, "CalcSZPC", "byte");
        var calcCarryByte = RequireMethod(flagsTree, "CalcCarryFlag", "byte");
        var parity = RequireMethod(flagsTree, "CalcParityFlag", "byte");
        var aux = RequireMethod(flagsTree, "CalcAuxCarryFlag", "byte", "byte");
        RequireFragments(calcSzpcByte, "this.CalcZeroFlag(val);", "this.CalcSignFlag(val);",
            "this.CalcParityFlag(val);", "this.CalcCarryFlag(val);");
        RequireFragments(calcCarryByte, "Carry = (data & 0x01) == 0x01;");
        RequireFragments(parity, "Parity = Utils.GetParity(result);");
        RequireFragments(aux, "AuxCarry = (byte)((a & 0x0f) + (b & 0x0f)) > 0x0f;");
        var getParity = RequireMethod(utilsTree, "GetParity", "byte");
        RequireFragments(getParity, "return 1 == ParityTable[val];");
        VerifyEvenParityTable(utilsTree);

        var equations = new[]
        {
            Equation("ANA_result", "and", 8, "A & operand", ana),
            Equation("XRA_result", "xor", 8, "A ^ operand", xor),
            Equation("ORA_B_result", "or", 8, "A | B", oraB),
            Equation("ORA_M_result", "or", 8, "A | M", oraM),
            Equation("AOR_result", "and", 8, "A & operand (source helper is named AOR but performs AND)", aor),
            Equation("ANA_CY_next", "constant", 1, "0", ana),
            Equation("ANA_AC_next", "constant", 1, "0 (source helper)", ana),
            Equation("XRA_CY_next", "bit", 1, "result8[0] (source CalcCarryFlag(byte) behavior)", calcCarryByte),
            Equation("XRA_AC_effect", "preserve", 1, "AC_prev (XOR does not write AC)", xor),
            Equation("ORA_B_CY_next", "constant", 1, "0", oraB),
            Equation("ORA_B_AC_next", "constant", 1, "0", oraB),
            Equation("ORA_M_CY_next", "bit", 1, "result8[0] (source CalcCarryFlag(byte) behavior)", calcCarryByte),
            Equation("ORA_M_AC_effect", "preserve", 1, "AC_prev (ORA_M does not write AC)", oraM),
            Equation("ORI_source_result", "and", 8, "A & data (AOR source helper defect)", ori),
            Equation("ORI_spec_result", "or", 8, "A | data (Intel 8080 ORI)", ori),
            Equation("XRI_source_result", "xor", 8, "A ^ data (XRI calls XOR helper)", xri),
            Equation("XRI_CY_source_next", "bit", 1, "result8[0] (source CalcCarryFlag(byte) behavior)", xri),
            Equation("XRI_AC_source_effect", "preserve", 1, "AC_prev (XRI calls XOR; XOR does not write AC)", xri),
            Equation("PC_XRI_source_next", "increment", 16, "PC + 2 (XOR helper increments once; XRI wrapper increments once)", xri),
            Equation("SZP", "flags", 3, "Z=(result8==0), S=result8[7], P=even_parity8(result8)", calcSzpcByte),
        };
        var traces = new[] { ana, xor, aor, oraB, oraM, ani, xri, ori, calcSzpcByte, calcCarryByte, parity, aux, getParity }
            .Select(method => Span(model, method)).ToArray();
        return new
        {
            schema = "hardwarebridge.intel8080.logical-hwir/v1",
            disposition = "source-snapshot-pinned logical projection; handler-specific source effects and architectural operation selection remain distinct",
            sourceHashes = new[] { SourceHash(model, opsPath), SourceHash(model, flagsPath), SourceHash(model, utilsPath) },
            dispatch = dispatch.Select(item => new { opcode = $"0x{item.Code:X2}", mnemonic = item.Mnemonic,
                sourceMethod = item.Method, sourceFile = opsPath,
                sourceLine = model.Opcodes.Single(op => op.Code == item.Code).Line }).ToArray(),
            equations,
            architectureSelection = new
            {
                ORI_operation = "A | data",
                ANA_auxiliaryCarry = "A[3] | operand[3] (RTL/manual selection; emulator ANA clears AC)",
                logicalCarry = "0 for architectural ANA/XRA/ORA",
                decision = "Use Intel 8080 operation and flag behavior for RTL; preserve source-helper observations in separate equations."
            },
            sourceTrace = traces,
            deltasAndLimits = new[]
            {
                "Source AOR computes AND; ORI_D calls AOR, so source-dispatched F6h ORI differs from Intel ORI.",
                "Source-dispatched EEh XRI calls the XOR helper; its result is XOR, but source CY follows result bit 0 and AC is preserved.",
                "Source XOR and ORA_M call CalcSZPC(byte), whose CalcCarryFlag(byte) sets CY to result bit 0; both leave AC unchanged.",
                "Source ORA_B explicitly clears CY and AC; source ANA clears CY and AC. RTL uses architectural logical flag behavior.",
                "CircuitWeave may lower only isolated 8-bit logical result equations; flags, opcode decode and CPU sequencing remain separate."
            }
        };
    }

    private static string RequireSource(ImportModel model, string fileName) =>
        model.Files.SingleOrDefault(file => Path.GetFileName(file.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))?.Path
        ?? throw new InvalidDataException($"Required source file missing from inventory: {fileName}.");

    private static void RequirePinnedSourceHash(ImportModel model, string path, string expectedSha256)
    {
        string actual = SourceHash(model, path);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Source-pinned projection snapshot changed for {Path.GetFileName(path)}: expected {expectedSha256}, found {actual}. Review and update the projection before exporting.");
    }

    private static MethodDeclarationSyntax RequireMethod(SyntaxTree tree, string name, params string[] parameterTypes)
    {
        var matches = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Select(p => p.Type?.ToString()).SequenceEqual(parameterTypes))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"Expected one {name}({string.Join(",", parameterTypes)}) method; found {matches.Length}.");
        return matches[0];
    }

    private static void RequireFragments(MethodDeclarationSyntax method, params string[] fragments)
    {
        string normalized = method.Body?.NormalizeWhitespace().ToFullString()
            ?? throw new InvalidDataException($"Expected block-bodied source method {method.Identifier.ValueText}.");
        foreach (string fragment in fragments)
            if (!normalized.Contains(fragment, StringComparison.Ordinal))
                throw new InvalidDataException($"Source semantic check failed in {method.Identifier.ValueText} at line {MethodLine(method)}: expected `{fragment}`.");
    }

    private static void VerifyEvenParityTable(SyntaxTree tree)
    {
        var table = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == "ParityTable")
            ?? throw new InvalidDataException("Utils.ParityTable source initializer missing.");
        var values = table.Initializer?.Value.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
            .Select(literal => literal.Token.Value).OfType<int>().ToArray() ?? Array.Empty<int>();
        if (values.Length != 256) throw new InvalidDataException($"Expected 256 parity entries, found {values.Length}.");
        for (int value = 0; value < 256; value++)
        {
            int bits = 0;
            for (int bit = 0; bit < 8; bit++) bits += (value >> bit) & 1;
            if (values[value] != ((bits & 1) == 0 ? 1 : 0))
                throw new InvalidDataException($"ParityTable entry {value:X2} is not even parity.");
        }
    }

    private static ProjectedEquation Equation(string name, string operation, int width, string formula, MethodDeclarationSyntax source) =>
        new(name, operation, width, formula, source.Identifier.ValueText,
            source.SyntaxTree.FilePath, MethodLine(source));

    private static int MethodLine(MethodDeclarationSyntax method) =>
        method.SyntaxTree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1;

    private static object Span(ImportModel model, MethodDeclarationSyntax method)
    {
        int start = method.SyntaxTree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
        int end = method.SyntaxTree.GetLineSpan(method.Span).EndLinePosition.Line + 1;
        return new { symbol = method.Identifier.ValueText, file = method.SyntaxTree.FilePath,
            sha256 = SourceHash(model, method.SyntaxTree.FilePath), startLine = start, endLine = end };
    }

    private static string SourceHash(ImportModel model, string path) =>
        model.Files.Single(file => Path.GetFullPath(file.Path).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).Sha256;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "packages", "node_modules" };

    public static string[] FindSourceFiles(string root)
    {
        var result = new List<string>(); var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs")) result.Add(Path.GetFullPath(file));
            foreach (string child in Directory.EnumerateDirectories(directory))
                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ImportModel Analyze(string sourceRoot, IReadOnlyCollection<string> selectedPaths)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        var files = new List<SourceFile>(); var types = new List<TypeModel>();
        var syntaxTrees = new List<SyntaxTree>(); var declarations = new List<(string File, BaseTypeDeclarationSyntax Node)>();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        foreach (string input in selectedPaths)
        {
            string path=Path.GetFullPath(input);
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected source is outside the declared 8080 source root: " + path);
            byte[] bytes=File.ReadAllBytes(path);
            var tree=CSharpSyntaxTree.ParseText(System.Text.Encoding.UTF8.GetString(bytes),parseOptions,path);
            syntaxTrees.Add(tree); files.Add(new(path,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),bytes.LongLength));
            var diagnostics=tree.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).Select(d=>d.GetMessage()).Take(8).ToArray();
            foreach(var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                declarations.Add((path,node));
                var members=node is TypeDeclarationSyntax td?td.Members.Select(m=>Describe(m,tree)).ToArray():Array.Empty<Member>();
                var warnings=new List<string>(diagnostics);
                if(node.DescendantNodes().Any(n=>n is InvocationExpressionSyntax))warnings.Add("Method calls need semantic/hardware mapping.");
                if(node.DescendantNodes().Any(n=>n is ArrayTypeSyntax or ArrayCreationExpressionSyntax))warnings.Add("Array-backed state requires an explicit hardware memory choice.");
                if(node.DescendantNodes().Any(n=>n is EventFieldDeclarationSyntax or EventDeclarationSyntax))warnings.Add("Events need signal-level interpretation.");
                if(node.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n=>n.Identifier.ValueText is "Dictionary" or "ConcurrentQueue" or "ILogger"))warnings.Add("Dictionary, queue, or logging is not directly hardware behavior.");
                var refs=node.DescendantNodes().OfType<TypeSyntax>().Select(t=>t.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray();
                types.Add(new(node.Identifier.ValueText,Namespace(node),node.Kind().ToString(),path,tree.GetLineSpan(node.Identifier.Span).StartLinePosition.Line+1,members,refs,warnings.Distinct().ToArray()));
            }
        }
        var references=AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString()?.Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)
            .Select(path=>(MetadataReference)MetadataReference.CreateFromFile(path)).ToArray() ?? Array.Empty<MetadataReference>();
        var compilation=CSharpCompilation.Create("Intel8080.SourceAnalysis",syntaxTrees,references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true));
        var compilationDiagnostics=compilation.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error)
            .Take(100).Select(d=>$"{d.Id}: {d.GetMessage()} ({d.Location.GetLineSpan().Path}:{d.Location.GetLineSpan().StartLinePosition.Line+1})").ToArray();
        var semanticReferences=new List<SemanticReference>();
        var memberDependencies=new List<MemberDependency>();
        foreach(var tree in syntaxTrees)
        {
            var semantic=compilation.GetSemanticModel(tree);
            foreach(var node in tree.GetRoot().DescendantNodes())
            {
                string? kind=null; ISymbol? symbol=null; SyntaxNode? referenceNode=null;
                switch(node)
                {
                    case InvocationExpressionSyntax invocation:
                        kind="method-call"; symbol=semantic.GetSymbolInfo(invocation).Symbol; referenceNode=invocation; break;
                    case ObjectCreationExpressionSyntax creation:
                        kind="object-creation"; symbol=semantic.GetSymbolInfo(creation).Symbol; referenceNode=creation; break;
                    case ImplicitObjectCreationExpressionSyntax creation:
                        kind="object-creation"; symbol=semantic.GetSymbolInfo(creation).Symbol; referenceNode=creation; break;
                    case IdentifierNameSyntax identifier:
                        symbol=semantic.GetSymbolInfo(identifier).Symbol;
                        if(symbol is IFieldSymbol)kind="field-reference";
                        else if(symbol is IPropertySymbol)kind="property-reference";
                        else if(symbol is IEventSymbol)kind="event-reference";
                        if(kind is not null)referenceNode=identifier;
                        break;
                }
                if(kind is null||referenceNode is null)continue;
                var location=symbol?.Locations.FirstOrDefault(l=>l.IsInSource);
                var source=tree.GetLineSpan(referenceNode.Span).StartLinePosition;
                var enclosing=referenceNode.Ancestors().FirstOrDefault(a=>a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax);
                string owner=enclosing switch
                {
                    MethodDeclarationSyntax m=>m.Identifier.ValueText,
                    ConstructorDeclarationSyntax c=>".ctor",
                    DestructorDeclarationSyntax d=>"~"+d.Identifier.ValueText,
                    OperatorDeclarationSyntax o=>"operator "+o.OperatorToken.ValueText,
                    ConversionOperatorDeclarationSyntax c=>"operator "+c.Type,
                    AccessorDeclarationSyntax a=>a.Keyword.ValueText,
                    PropertyDeclarationSyntax p=>p.Identifier.ValueText,
                    FieldDeclarationSyntax f=>string.Join(",",f.Declaration.Variables.Select(v=>v.Identifier.ValueText)),
                    _=>"<type-initializer-or-member>"
                };
                var ownerType=referenceNode.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if(ownerType is not null)owner=ownerType.Identifier.ValueText+"."+owner;
                var targetFile=location?.SourceTree?.FilePath;
                int? targetLine=location is null?null:location.SourceTree!.GetLineSpan(location.SourceSpan).StartLinePosition.Line+1;
                memberDependencies.Add(new(kind,owner,tree.FilePath,source.Line+1,referenceNode.ToString(),symbol?.ToDisplayString(),targetFile,targetLine));
                if(kind=="method-call")semanticReferences.Add(new(tree.FilePath,source.Line+1,referenceNode.ToString(),symbol?.ToDisplayString(),targetFile,targetLine));
            }
        }
        var opcodes = new List<Opcode>();
        foreach (var (file,node) in declarations.Where(x=>x.Node is ClassDeclarationSyntax && x.Node.Identifier.ValueText=="Cpu"))
        {
            var semantic=compilation.GetSemanticModel(node.SyntaxTree);
            var initializer=node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault(a=>a.Left is IdentifierNameSyntax id && id.Identifier.ValueText=="_ops")?.Right as ObjectCreationExpressionSyntax;
            var dictionary=initializer?.Initializer;
            if(dictionary is null) continue;
            foreach(var entry in dictionary.Expressions.OfType<InitializerExpressionSyntax>().Where(x=>x.Expressions.Count==2))
            {
                var constant=semantic.GetConstantValue(entry.Expressions[0]);
                if(!constant.HasValue||constant.Value is not IConvertible convertible) continue;
                byte code; try { code=convertible.ToByte(System.Globalization.CultureInfo.InvariantCulture); } catch(OverflowException) { continue; }
                var target=semantic.GetSymbolInfo(entry.Expressions[1]).Symbol as IMethodSymbol;
                string method=target?.Name ?? entry.Expressions[1].ToString().Split('.').Last(); var line=entry.SyntaxTree.GetLineSpan(entry.Span).StartLinePosition.Line+1;
                opcodes.Add(new(code,method,target is not null,file,line));
            }
        }
        var decision = new[]{"8080 address width 16; data width 8 (architecture assumption)","clock/reset, memory, I/O and IRQ contracts are design decisions; emulator C# does not establish cycle timing","Roslyn semantic compilation is in-memory for symbol binding; no project build, emit or source execution"};
        var unsupported = new[]{"Dictionary delegates/control flow, queue interrupts, events, logger and byte-array memory require manual hardware mapping","No generic C# to MLIR/RTL lowering is performed by this analyzer","Opcode syntax scan is not functional instruction verification"};
        return new("hardwarebridge.intel8080.import/v2","Roslyn in-memory semantic compilation using SDK trusted platform assemblies; diagnostics retained; no emit, project build, source execution or hardware elaboration",sourceRoot,files,types,opcodes.OrderBy(x=>x.Code).ToArray(),semanticReferences,memberDependencies,compilationDiagnostics,decision,unsupported);
    }

    private static Member Describe(MemberDeclarationSyntax m, SyntaxTree tree)
    {
        var (name,kind,sig)=m switch
        {
            MethodDeclarationSyntax x=>(x.Identifier.ValueText,"method",$"{x.ReturnType} {x.Identifier}{x.TypeParameterList}{x.ParameterList}"),
            ConstructorDeclarationSyntax x=>(x.Identifier.ValueText,"constructor",$"{x.Identifier}{x.ParameterList}"),
            FieldDeclarationSyntax x=>(string.Join(",",x.Declaration.Variables.Select(v=>v.Identifier.ValueText)),"field",x.Declaration.ToString()),
            PropertyDeclarationSyntax x=>(x.Identifier.ValueText,"property",$"{x.Type} {x.Identifier}"),
            EventFieldDeclarationSyntax x=>(string.Join(",",x.Declaration.Variables.Select(v=>v.Identifier.ValueText)),"event",x.Declaration.ToString()),
            _=>(m.Kind().ToString(),"member",m.Kind().ToString())
        };
        var pos=tree.GetLineSpan(m.Span).StartLinePosition; return new(name,kind,sig,pos.Line+1,pos.Character+1);
    }
    private static string Namespace(SyntaxNode n)=>n.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()??"";
}
