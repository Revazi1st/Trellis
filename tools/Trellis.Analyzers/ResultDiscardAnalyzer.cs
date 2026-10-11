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

    private const string Title = "The result of this call is discarded";
    private const string Category = "Reliability";
    private const string HelpLink = "docs/adr/0030-discarded-result-analyzer.md";
    /// <summary>How deep <see cref="CarriesResult"/> looks through wrappers: <c>Task&lt;Result[]&gt;</c> is two levels.</summary>
    private const int MaxNesting = 4;

    private const string Description =
        "A Result carries the failure of the operation that produced it. Dropping it commits a refused change or hides a failed step as if it had succeeded.";

    /// <summary>A statement computes a result and throws it away.</summary>
    internal static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: "This {0} is discarded: handle it or return it, or write '_ = ...' with a one-line reason to drop it on purpose",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description,
        helpLinkUri: HelpLink);

    /// <summary>
    /// A task of a result becomes a plain <c>Task</c>: whoever awaits it learns that it finished, never
    /// whether it failed. The same id, so one severity line and one ADR cover both.
    /// </summary>
    internal static readonly DiagnosticDescriptor TaskRule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: "This {0} is converted to {1}, which drops its result: keep the {0} (return it, or await it and handle the result)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description,
        helpLinkUri: HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule, TaskRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeExpressionStatement, OperationKind.ExpressionStatement);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeDelegateCreation, OperationKind.DelegateCreation);
    }

    private static void AnalyzeExpressionStatement(OperationAnalysisContext context)
    {
        IOperation statementValue = ((IExpressionStatementOperation)context.Operation).Operation;
        if (DroppedValue(statementValue) is { Type: { } type } dropped
            && CarriesResult(type)
            && !IsCompilersUnawaitedCall(context, dropped, type)
            && !IsSubstituteCallCheck(dropped)
            && !IsExpectedToThrow(context.Operation))
        {
            Report(context, Rule, statementValue.Syntax, type);
        }
    }

    /// <summary>
    /// <c>Task&lt;Result&gt;</c> to <c>Task</c> is an implicit reference conversion, so a member, lambda
    /// or variable typed <c>Task</c> swallows the result without a word: <c>public Task Handle(…) =&gt;
    /// crm.PushPaymentAsync(…);</c> lets the outbox checkpoint a failed push as done. Reported when the
    /// converted value is computed on the spot; a task already held in a variable keeps its result there.
    /// </summary>
    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.Type is { } target
            && IsPlainTask(target)
            && conversion.Operand.Type is { } source
            && CarriesResult(source)
            && DroppedValue(conversion.Operand) is { } computed
            && !IsSubstituteCallCheck(computed)
            && !IsExpectedToThrow(conversion))
        {
            Report(context, TaskRule, conversion.Operand.Syntax, source, target);
        }
    }

    /// <summary>The method-group form of the same conversion: <c>Func&lt;Task&gt; work = port.PushAsync;</c>.</summary>
    private static void AnalyzeDelegateCreation(OperationAnalysisContext context)
    {
        var creation = (IDelegateCreationOperation)context.Operation;
        if (creation.Target is IMethodReferenceOperation { Method.ReturnType: { } returned } reference
            && creation.Type is INamedTypeSymbol { DelegateInvokeMethod.ReturnType: { } delegateReturns }
            && IsPlainTask(delegateReturns)
            && CarriesResult(returned)
            && !IsExpectedToThrow(creation))
        {
            Report(context, TaskRule, reference.Syntax, returned, delegateReturns);
        }
    }

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor rule, SyntaxNode at, params ITypeSymbol[] types)
        => context.ReportDiagnostic(Diagnostic.Create(
            rule,
            at.GetLocation(),
            types.Select(static type => (object)type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).ToArray()));

    /// <summary>
    /// The expression body of a delegate handed to an exception assertion (<c>Should.ThrowAsync(() =&gt;
    /// handler.Handle(…))</c>, xUnit's <c>Assert.ThrowsAsync</c>, Shouldly's <c>ShouldThrow</c>), or the task
    /// handed to one: the call is expected to throw, so there is no result to lose. Matched by a method
    /// name that starts with <c>Throw</c> or <c>ShouldThrow</c>; only the delegate's own expression body is
    /// exempt, never the statements of a block body.
    /// </summary>
    private static bool IsExpectedToThrow(IOperation operation)
    {
        IOperation current = operation;
        if (current.Parent is IReturnOperation { IsImplicit: true } implicitReturn)
        {
            current = implicitReturn;
        }

        if (current.IsImplicit && current.Parent is IBlockOperation { IsImplicit: true, Parent: IAnonymousFunctionOperation lambda })
        {
            current = lambda;
        }

        while (current.Parent is IDelegateCreationOperation or IConversionOperation)
        {
            current = current.Parent;
        }

        return current.Parent is IArgumentOperation { Parent: IInvocationOperation { TargetMethod.Name: { } assertion } }
               && (assertion.StartsWith("Throw", StringComparison.Ordinal) || assertion.StartsWith("ShouldThrow", StringComparison.Ordinal));
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

    /// <summary>
    /// An unawaited call returning a task of a result, written as the statement itself inside an async
    /// method, lambda or local function: the compiler's CS4014 reports exactly that, so RSLT001 stays
    /// quiet there and reports the task everywhere CS4014 is silent (a sync method, a non-async lambda,
    /// a conditional access).
    /// </summary>
    private static bool IsCompilersUnawaitedCall(OperationAnalysisContext context, IOperation dropped, ITypeSymbol type)
        => dropped is IInvocationOperation
           && ReferenceEquals(dropped, ((IExpressionStatementOperation)context.Operation).Operation)
           && type is INamedTypeSymbol named
           && TaskValue(named) is not null
           && IsInAsyncFunction(context);

    private static bool IsInAsyncFunction(OperationAnalysisContext context)
    {
        for (IOperation? enclosing = context.Operation.Parent; enclosing is not null; enclosing = enclosing.Parent)
        {
            switch (enclosing)
            {
                case IAnonymousFunctionOperation lambda:
                    return lambda.Symbol.IsAsync;
                case ILocalFunctionOperation localFunction:
                    return localFunction.Symbol.IsAsync;
            }
        }

        return context.ContainingSymbol is IMethodSymbol { IsAsync: true };
    }

    /// <summary>
    /// A result, or a value that holds results the caller can only reach through it: the value of a
    /// <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> (<c>Task.WhenAny</c>), an array element
    /// (<c>Task.WhenAll</c>), a sequence element (an iterator that never runs until someone reads it) or a
    /// tuple element.
    /// </summary>
    private static bool CarriesResult(ITypeSymbol type, int depth = 0)
    {
        if (depth > MaxNesting || type.SpecialType != SpecialType.None)
        {
            return false;
        }

        if (IsResultType(type))
        {
            return true;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                return CarriesResult(array.ElementType, depth + 1);
            case INamedTypeSymbol { IsTupleType: true } tuple:
                return tuple.TupleElements.Any(element => CarriesResult(element.Type, depth + 1));
            case INamedTypeSymbol named:
                return (TaskValue(named) ?? SequenceElement(named)) is { } inner && CarriesResult(inner, depth + 1);
            default:
                return false;
        }
    }

    /// <summary>The non-generic <c>Task</c>, or an array or sequence of them: a shape that carries no result.</summary>
    private static bool IsPlainTask(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => IsPlainTask(array.ElementType),
        INamedTypeSymbol { Arity: 0, Name: "Task" } task => IsSystemThreadingTasks(task.ContainingNamespace),
        INamedTypeSymbol named => SequenceElement(named) is { } element && IsPlainTask(element),
        _ => false,
    };

    /// <summary>The <c>T</c> of a <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>.</summary>
    private static ITypeSymbol? TaskValue(INamedTypeSymbol type)
        => type is { Arity: 1, Name: "Task" or "ValueTask" } && IsSystemThreadingTasks(type.ContainingNamespace)
            ? type.TypeArguments[0]
            : null;

    /// <summary>The <c>T</c> of a type that is or implements <c>IEnumerable&lt;T&gt;</c> or <c>IAsyncEnumerable&lt;T&gt;</c>.</summary>
    private static ITypeSymbol? SequenceElement(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces.Insert(0, type))
        {
            if (candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                || candidate is { Arity: 1, Name: "IAsyncEnumerable", ContainingNamespace: { Name: "Generic", ContainingNamespace: { Name: "Collections", ContainingNamespace.Name: "System" } } })
            {
                return candidate.TypeArguments[0];
            }
        }

        return null;
    }

    private static bool IsSystemThreadingTasks(INamespaceSymbol? ns)
        => ns is { Name: "Tasks", ContainingNamespace: { Name: "Threading", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } };

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
