using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trellis.Analyzers;

/// <summary>
/// RSLT001 (ADR-0030): a statement computes a <c>Result</c>, a <c>Result&lt;T&gt;</c> or an ASP.NET Core
/// Identity <c>IdentityResult</c> and drops it. A dropped Result is a failure nobody reads:
/// <c>invoice.Issue(now); return Result.Success();</c> commits a refused transition and answers 2xx.
/// </summary>
/// <remarks>
/// Matching is by type, not by method name, so every aggregate method, value-object factory, handler
/// and combinator is covered without a list, and an awaited <c>Task&lt;Result&gt;</c> is seen through the
/// <c>await</c>. The SharedKernel types match by name and by a containing namespace ending in
/// <c>SharedKernel.Results</c>, which keeps working after the template renames the root namespace.
/// Reported: an invocation, an object creation, an awaited task of a result, and a conditional access
/// ending in one of those, written as a statement, including the implicit statement of an
/// expression-bodied lambda, local function or member that returns void. Not reported: an assignment
/// (so <c>_ = …</c> is the way to drop one on purpose), a return, an argument, generated code, and
/// NSubstitute's received-call check (<c>await uow.Received(1).CommitAsync(ct)</c>), which asserts a
/// call rather than making one.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResultDiscardAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RSLT001";

    internal static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "The result of this call is discarded",
        messageFormat: "This {0} is discarded: handle it or return it, or write '_ = ...' with a one-line reason to drop it on purpose",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A Result carries the failure of the operation that produced it. Dropping it commits a refused change or hides a failed step as if it had succeeded.",
        helpLinkUri: "docs/adr/0030-discarded-result-analyzer.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeExpressionStatement, OperationKind.ExpressionStatement);
    }

    private static void AnalyzeExpressionStatement(OperationAnalysisContext context)
    {
        IOperation statementValue = ((IExpressionStatementOperation)context.Operation).Operation;
        IOperation? dropped = DroppedValue(statementValue);
        ITypeSymbol? type = dropped?.Type;
        if (type is not null && IsResultType(type) && !IsSubstituteCallCheck(dropped!))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                statementValue.Syntax.GetLocation(),
                type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    /// <summary>
    /// The value a statement computes and throws away, or null when the statement stores it (any
    /// assignment, the explicit discard included) or computes nothing. A conditional access drops
    /// whatever its last link computes, so <c>a?.B()</c> counts and the C# 14 <c>a?.B = c</c> does not.
    /// </summary>
    private static IOperation? DroppedValue(IOperation operation)
    {
        while (operation is IConditionalAccessOperation conditionalAccess)
        {
            operation = conditionalAccess.WhenNotNull;
        }

        return operation.Kind is OperationKind.Invocation or OperationKind.ObjectCreation or OperationKind.Await
            ? operation
            : null;
    }

    /// <summary>
    /// NSubstitute's call check, <c>await unitOfWork.Received(1).CommitAsync(ct)</c>: the call on the
    /// object <c>Received</c>, <c>DidNotReceive</c>, <c>ReceivedWithAnyArgs</c> or
    /// <c>DidNotReceiveWithAnyArgs</c> returns asserts that the call happened (or did not) and returns a
    /// placeholder, so there is no outcome to drop. Matched by method name and the NSubstitute namespace.
    /// </summary>
    private static bool IsSubstituteCallCheck(IOperation dropped)
    {
        if (dropped is IAwaitOperation awaited)
        {
            dropped = awaited.Operation;
        }

        IOperation? receiver = (dropped as IInvocationOperation)?.Instance;
        while (receiver is IConversionOperation conversion)
        {
            receiver = conversion.Operand;
        }

        return receiver is IInvocationOperation { TargetMethod: { } check }
               && check.Name is "Received" or "DidNotReceive" or "ReceivedWithAnyArgs" or "DidNotReceiveWithAnyArgs"
               && RootNamespaceName(check.ContainingNamespace) == "NSubstitute";
    }

    private static string? RootNamespaceName(INamespaceSymbol? ns)
    {
        while (ns is { ContainingNamespace.IsGlobalNamespace: false })
        {
            ns = ns.ContainingNamespace;
        }

        return ns is { IsGlobalNamespace: false } ? ns.Name : null;
    }

    private static bool IsResultType(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol typeParameter)
        {
            return typeParameter.ConstraintTypes.Any(IsResultType);
        }

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (IsSharedKernelResult(current) || IsIdentityResult(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>Result</c> or <c>Result&lt;T&gt;</c> in a namespace ending in <c>SharedKernel.Results</c>.</summary>
    private static bool IsSharedKernelResult(ITypeSymbol type)
        => type.Name == "Result"
           && type.ContainingNamespace is { Name: "Results", ContainingNamespace.Name: "SharedKernel" };

    /// <summary><c>Microsoft.AspNetCore.Identity.IdentityResult</c>: a failed role or user write that nobody checks.</summary>
    private static bool IsIdentityResult(ITypeSymbol type)
        => type.Name == "IdentityResult"
           && type.ContainingNamespace is
           {
               Name: "Identity",
               ContainingNamespace:
               {
                   Name: "AspNetCore",
                   ContainingNamespace: { Name: "Microsoft", ContainingNamespace.IsGlobalNamespace: true },
               },
           };
}
