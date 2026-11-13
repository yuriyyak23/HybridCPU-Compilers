#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-call prefix only. No clock, reset, stall or scheduling contract is inferred.</summary>
public sealed record SchedulerCounterCallCandidate(HwValidatedDesign Design, string SchedulerSha256,
    string MethodSha256, int MethodLine, int MethodColumn, string Scope);

public static class BridgeHwirSchedulerCounterCall
{
    public static SchedulerCounterCallCandidate Compile(byte[] schedulerBytes, byte[] methodBytes,
        string expectedSchedulerSha256, string expectedMethodSha256, string baseCommit, HwToolchainIdentity toolchain, bool checkedOverflow)
    {
        static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if (checkedOverflow || schedulerBytes.Length > 2_000_000 || methodBytes.Length > 2_000_000 ||
            Hash(schedulerBytes) != expectedSchedulerSha256 || Hash(methodBytes) != expectedMethodSha256 ||
            baseCommit.Length != 40 || baseCommit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Scheduler source/configuration identity differs or overflow is checked.");
        var utf8 = new UTF8Encoding(false, true); var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var schedulerTree = CSharpSyntaxTree.ParseText(utf8.GetString(schedulerBytes), parse);
        var methodTree = CSharpSyntaxTree.ParseText(utf8.GetString(methodBytes), parse);
        static ClassDeclarationSyntax FindClass(SyntaxTree tree) => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core" && c.Modifiers.Any(SyntaxKind.PartialKeyword));
        var owner = FindClass(schedulerTree); var methodOwner = FindClass(methodTree);
        var field = owner.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "GlobalCycleCounter"));
        if (field.Declaration.Type.ToString() != "ulong" || field.Declaration.Variables.Count != 1 ||
            field.Declaration.Variables[0].Initializer?.Value.ToString() != "0" || !field.Modifiers.Any(SyntaxKind.PublicKeyword) || field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.VolatileKeyword)))
            throw new InvalidDataException("Scheduler counter field changed.");
        var method = methodOwner.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "PackBundleIntraCoreSmt" && m.ParameterList.Parameters.Count == 5);
        if (method.Body?.Statements is not [IfStatementSyntax guard, ExpressionStatementSyntax increment, ..] ||
            guard.Condition.NormalizeWhitespace().ToFullString() != "bundle == null || bundle.Count != 8" ||
            guard.Statement is not ReturnStatementSyntax || increment.Expression is not PostfixUnaryExpressionSyntax post ||
            !post.IsKind(SyntaxKind.PostIncrementExpression) || post.Operand.ToString() != "GlobalCycleCounter")
            throw new InvalidDataException("Scheduler call guard or increment changed.");
        // Rebind the captured prefix in a minimal context. This confirms symbols
        // for the projection only, not the full source project or hardware clock.
        var projected = method.WithBody(SyntaxFactory.Block(guard, increment, SyntaxFactory.ParseStatement("return null!;")));
        var text = "#nullable enable\nusing System; using System.Collections.Generic; namespace YAKSys_Hybrid_CPU.Memory { public class MemorySubsystem {} } " +
            "namespace YAKSys_Hybrid_CPU.Core { public class MicroOp {} public partial class MicroOpScheduler { " + field.ToFullString() + projected.ToFullString() + " } }";
        var projectionTree = CSharpSyntaxTree.ParseText(text, parse);
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("SchedulerCallProjection", [projectionTree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow: false));
        if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("Scheduler prefix projection does not bind.");
        var type = compilation.GetTypeByMetadataName("YAKSys_Hybrid_CPU.Core.MicroOpScheduler")!;
        var boundField = type.GetMembers("GlobalCycleCounter").OfType<IFieldSymbol>().Single();
        var boundMethod = type.GetMembers("PackBundleIntraCoreSmt").OfType<IMethodSymbol>().Single();
        var methodSyntax = (MethodDeclarationSyntax)boundMethod.DeclaringSyntaxReferences.Single().GetSyntax();
        var boundIncrement = (ExpressionStatementSyntax)methodSyntax.Body!.Statements[1];
        var model = compilation.GetSemanticModel(projectionTree);
        var condition = ((IfStatementSyntax)methodSyntax.Body.Statements[0]).Condition;
        var countAccess = condition.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Single();
        if (boundMethod.Parameters[0].Name != "bundle" ||
            model.GetSymbolInfo(countAccess).Symbol is not IPropertySymbol countProperty || countProperty.Name != "Count" ||
            countProperty.Type.SpecialType != SpecialType.System_Int32 ||
            countProperty.ContainingType.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.IReadOnlyCollection<T>" ||
            !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(countAccess.Expression).Symbol, boundMethod.Parameters[0]))
            throw new InvalidDataException("Scheduler bundle count does not bind to the projected input parameter.");
        if (model.GetOperation(boundIncrement.Expression) is not IIncrementOrDecrementOperation op || op.IsChecked ||
            op.Target is not IFieldReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Field, boundField))
            throw new InvalidDataException("Counter increment does not bind to unchecked captured field.");
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var digest = Hash(Encoding.UTF8.GetBytes("scheduler-call-prefix/v1|" + expectedSchedulerSha256 + "|" + expectedMethodSha256 + "|unchecked"));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs", "CircuitWeave.SchedulerCall.Projection",
            "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "PackBundleIntraCoreSmt", digest, HwSourceRole.Primary, pos.Line + 1, pos.Character + 1);
        var u1 = new HwBitVectorType(1); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        HwPortContract[] ports = [new("bundle_present",HwPortDirection.Input,u1), new("bundle_count",HwPortDirection.Input,u32),
            new("counter",HwPortDirection.Input,u64), new("next_counter",HwPortDirection.Output,u64)];
        HwNode[] nodes = [new HwConstantNode("eight",u32,Convert.ToString(8,2).PadLeft(32,'0'),origin),
            new HwConstantNode("one",u64,Convert.ToString(1,2).PadLeft(64,'0'),origin),
            new HwOperationNode("count_is_eight","eq",["bundle_count","eight"],u1,Origin:origin),
            new HwOperationNode("valid","and",["bundle_present","count_is_eight"],u1,Origin:origin),
            new HwOperationNode("incremented","add",["counter","one"],u64,Origin:origin),
            new HwMuxNode("next","valid","incremented","counter",u64,origin), new HwOutputDriveNode("drive","next_counter","next",origin)];
        var contract = new HwHardwareContract("scheduler.counter-call-prefix.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1));
        var module = new HwModule("scheduler_counter_call_prefix","scheduler_counter_call_prefix",ports,nodes,[],contract,origin);
        var design = new HwDesign("hwir/v0.1",module.Id,[module],toolchain with { HardwareLanguageProfile = "hwp-hwir-comb-v1" },[]);
        var validated = new FailClosedHardwareIrValidator().Validate(design);
        if (!validated.Accepted || validated.Validated is null) throw new InvalidDataException("Scheduler prefix HWIR refused: " + string.Join("; ", validated.Diagnostics.Select(d => d.Message)));
        return new(validated.Validated, expectedSchedulerSha256, expectedMethodSha256, pos.Line + 1, pos.Character + 1,
            "Call prefix only; pure bundle presence/count projection, unchecked increment; no clock/reset/stall, exception or scheduler equivalence.");
    }
}
#endif
