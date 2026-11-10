using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HybridCPU.Compiler.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KernelNoHeapAnalyzer : DiagnosticAnalyzer
{
    private readonly Func<IMethodSymbol, bool>? _ownedConstructorAdmission;
    public KernelNoHeapAnalyzer() { }
    public KernelNoHeapAnalyzer(Func<IMethodSymbol, bool> ownedConstructorAdmission) =>
        _ownedConstructorAdmission = ownedConstructorAdmission ?? throw new ArgumentNullException(nameof(ownedConstructorAdmission));
    public const string ProfileAttributeMetadataName = "HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute";
    private static readonly DiagnosticDescriptor Allocation = Rule("HC0004",
        "Managed heap allocation is not permitted", "'{0}' allocates in HybridCPU.Kernel.NoHeap.");
    private static readonly DiagnosticDescriptor RuntimeFeature = Rule("HC0005",
        "Runtime feature is not permitted", "'{0}' is not permitted in HybridCPU.Kernel.NoHeap.");
    private static readonly DiagnosticDescriptor LanguageFeature = Rule("HC0006",
        "Language feature is not permitted", "'{0}' is not permitted in HybridCPU.Kernel.NoHeap.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Allocation, RuntimeFeature, LanguageFeature];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeAllocation,
            SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression, SyntaxKind.ArrayCreationExpression,
            SyntaxKind.ImplicitArrayCreationExpression, SyntaxKind.AnonymousObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeForbiddenLanguage,
            SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.AnonymousMethodExpression, SyntaxKind.AwaitExpression,
            SyntaxKind.InterpolatedStringExpression);
        context.RegisterSyntaxNodeAction(AnalyzeStringConcatenation, SyntaxKind.AddExpression);
        context.RegisterSyntaxNodeAction(AnalyzeCollectionExpression, SyntaxKind.CollectionExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeMethodGroup, OperationKind.DelegateCreation);
        context.RegisterOperationAction(AnalyzeSwitchExpression, OperationKind.SwitchExpression);
        context.RegisterOperationAction(AnalyzeDynamic, OperationKind.DynamicInvocation,
            OperationKind.DynamicMemberReference, OperationKind.DynamicIndexerAccess,
            OperationKind.DynamicObjectCreation);
    }

    private void AnalyzeAllocation(SyntaxNodeAnalysisContext context)
    {
        if (!IsProfiled(context)) return;
        if (context.Node is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax &&
            context.SemanticModel.GetOperation(context.Node, context.CancellationToken) is IObjectCreationOperation creation &&
            creation.Type?.IsValueType == true)
        {
            if (IsExactNullableInt32Construction(creation) || IsBoundedScalarConstruction(context, creation) ||
                creation.Initializer is null && creation.Constructor is { DeclaringSyntaxReferences.Length: 0 } constructor &&
                _ownedConstructorAdmission?.Invoke(constructor) == true) return;
            context.ReportDiagnostic(Diagnostic.Create(LanguageFeature, context.Node.GetLocation(), "unqualified value construction"));
            return;
        }
        context.ReportDiagnostic(Diagnostic.Create(Allocation, context.Node.GetLocation(), context.Node.Kind().ToString()));
    }

    private static bool IsExactNullableInt32Construction(IObjectCreationOperation creation) =>
        creation.Initializer is null && creation.Type is INamedTypeSymbol type &&
        type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type.TypeArguments.Length == 1 &&
        type.TypeArguments[0].SpecialType == SpecialType.System_Int32 &&
        creation.Constructor is { MethodKind: MethodKind.Constructor, IsStatic: false, Parameters.Length: 1 } constructor &&
        constructor.Parameters[0].RefKind == RefKind.None && constructor.Parameters[0].Type.SpecialType == SpecialType.System_Int32;

    private static bool IsBoundedScalarConstruction(SyntaxNodeAnalysisContext context, IObjectCreationOperation creation)
    {
        // Source-only admission for one pure Int32 field copy. This grants no storage,
        // receiver, ABI or lowering facts: the existing importer must rederive those from CIL.
        if (creation.Initializer is not null || creation.Type is not INamedTypeSymbol
            { TypeKind: TypeKind.Struct, Arity: 0, ContainingType: null, IsRecord: false, IsRefLikeType: false } type ||
            type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute") ||
            type.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.StaticConstructor) ||
            creation.Constructor is not { Parameters.Length: 1, DeclaringSyntaxReferences.Length: 1 } constructor ||
            constructor.Parameters[0].Type.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64)) return false;
        IFieldSymbol[] fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length != 1 || fields[0].IsFixedSizeBuffer ||
            !SymbolEqualityComparer.Default.Equals(fields[0].Type, constructor.Parameters[0].Type) ||
            fields[0].DeclaringSyntaxReferences.Any(r => r.GetSyntax(context.CancellationToken) is VariableDeclaratorSyntax { Initializer: not null }))
            return false;
        ISymbol assignedStorage = fields[0];
        if (fields[0].IsImplicitlyDeclared)
        {
            // An assignment to a get-only auto-property in its own constructor is
            // a backing-field copy, not a setter call. Qualify the exact source
            // declaration before allowing that additional spelling.
            if (!fields[0].IsReadOnly || fields[0].AssociatedSymbol is not IPropertySymbol
                { IsStatic: false, IsIndexer: false, SetMethod: null, DeclaringSyntaxReferences.Length: 1 } property ||
                property.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) is not PropertyDeclarationSyntax
                { Initializer: null, ExpressionBody: null, AccessorList.Accessors.Count: 1 } propertyDeclaration ||
                propertyDeclaration.AccessorList!.Accessors[0] is not
                { Body: null, ExpressionBody: null } getter || !getter.IsKind(SyntaxKind.GetAccessorDeclaration))
                return false;
            assignedStorage = property;
        }
        if (constructor.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) is not ConstructorDeclarationSyntax
            { Initializer: null, ExpressionBody: null, Body.Statements.Count: 1 } declaration ||
            declaration.Body!.Statements[0] is not ExpressionStatementSyntax
            { Expression: AssignmentExpressionSyntax assignment } || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
        SemanticModel model = context.Compilation.GetSemanticModel(declaration.SyntaxTree);
        return SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol, assignedStorage) &&
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Right, context.CancellationToken).Symbol, constructor.Parameters[0]);
    }

    private static void AnalyzeCollectionExpression(SyntaxNodeAnalysisContext context)
    {
        if (!IsProfiled(context)) return;
        ITypeSymbol? target = context.SemanticModel.GetTypeInfo(context.Node, context.CancellationToken).ConvertedType;
        // Reference targets allocate; value targets can invoke builders or spread lowering.
        // Do not certify those as stack-only without a separate lowering contract.
        DiagnosticDescriptor rule = target?.IsReferenceType == true ? Allocation : LanguageFeature;
        context.ReportDiagnostic(Diagnostic.Create(rule, context.Node.GetLocation(), "collection expression"));
    }

    private static void AnalyzeForbiddenLanguage(SyntaxNodeAnalysisContext context)
    {
        if (!IsProfiled(context)) return;
        string feature = context.Node.Kind() switch
        {
            SyntaxKind.AwaitExpression => "async/await",
            SyntaxKind.InterpolatedStringExpression => "interpolated string",
            _ => "delegate or closure creation"
        };
        context.ReportDiagnostic(Diagnostic.Create(LanguageFeature, context.Node.GetLocation(), feature));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (!IsProfiled(context) || context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        string owner = method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        string identity = owner + "." + method.Name;
        if (method.ContainingType.SpecialType == SpecialType.System_Array && method.Name == "Empty" &&
            method.IsStatic && method.Arity == 1 && method.Parameters.Length == 0)
        {
            // Its cached singleton still requires an allocation on first initialization.
            context.ReportDiagnostic(Diagnostic.Create(Allocation, context.Node.GetLocation(), "Array.Empty cached array creation"));
            return;
        }
        if (owner is "System.GC" or "System.Threading.Thread" or "System.Threading.ThreadPool" ||
            owner.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
            owner.StartsWith("System.Linq.", StringComparison.Ordinal) ||
            identity is "System.Activator.CreateInstance" or "System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject" or
                "System.Delegate.CreateDelegate" ||
            identity.StartsWith("System.Threading.Tasks.Task.", StringComparison.Ordinal) ||
            identity.StartsWith("System.Reflection.Assembly.Load", StringComparison.Ordinal))
            context.ReportDiagnostic(Diagnostic.Create(RuntimeFeature, context.Node.GetLocation(), identity));
    }

    private static void AnalyzeStringConcatenation(SyntaxNodeAnalysisContext context)
    {
        if (!IsProfiled(context) || context.Node is not BinaryExpressionSyntax expression ||
            context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type?.SpecialType != SpecialType.System_String) return;
        context.ReportDiagnostic(Diagnostic.Create(Allocation, expression.GetLocation(), "string concatenation"));
    }

    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        if (!IsProfiled(context.ContainingSymbol, context.CancellationToken) || context.Operation is not IConversionOperation conversion ||
            conversion.Operand.Type?.IsValueType != true || conversion.Type?.IsReferenceType != true) return;
        context.ReportDiagnostic(Diagnostic.Create(Allocation, context.Operation.Syntax.GetLocation(), "boxing conversion"));
    }

    private static void AnalyzeDynamic(OperationAnalysisContext context)
    {
        if (!IsProfiled(context.ContainingSymbol, context.CancellationToken)) return;
        context.ReportDiagnostic(Diagnostic.Create(LanguageFeature, context.Operation.Syntax.GetLocation(), "dynamic"));
    }

    private static void AnalyzeMethodGroup(OperationAnalysisContext context)
    {
        if (!IsProfiled(context.ContainingSymbol, context.CancellationToken) ||
            context.Operation is not IDelegateCreationOperation { Target: IMethodReferenceOperation } ||
            context.Operation.Syntax is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            return;
        // Explicit new and anonymous functions retain their existing syntax diagnostics.
        // A cached static delegate still allocates on its first construction.
        context.ReportDiagnostic(Diagnostic.Create(Allocation, context.Operation.Syntax.GetLocation(),
            "method-group delegate creation"));
    }

    private static void AnalyzeSwitchExpression(OperationAnalysisContext context)
    {
        if (!IsProfiled(context.ContainingSymbol, context.CancellationToken) ||
            context.Operation is not ISwitchExpressionOperation { IsExhaustive: false }) return;
        // The implicit failure path constructs an exception and can box the input.
        // Named enum members alone do not cover unnamed values; guarded discard is not exhaustive.
        context.ReportDiagnostic(Diagnostic.Create(Allocation, context.Operation.Syntax.GetLocation(),
            "nonexhaustive switch expression failure path"));
    }

    private static bool IsProfiled(SyntaxNodeAnalysisContext context)
        => IsProfiled(context.ContainingSymbol, context.CancellationToken);

    private static bool IsProfiled(ISymbol? owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (owner is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == ProfileAttributeMetadataName))
                return true;
            owner = owner.ContainingSymbol;
        }
        return false;
    }

    private static DiagnosticDescriptor Rule(string id, string title, string message) => new(
        id, title, message, "HybridCPU.Profile", DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Source-profile diagnostics are conservative. CIL importer and whole-program admission remain mandatory.");
}
