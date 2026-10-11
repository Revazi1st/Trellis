using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trellis.Analyzers;

/// <summary>
/// RSLT001 (ADR-0030): a statement computes a result (the SharedKernel <c>Result</c>, <c>Result&lt;T&gt;</c>
/// or <c>Error</c>, the UI's <c>ApiResult&lt;T&gt;</c>, ASP.NET Core Identity's <c>IdentityResult</c>) and
/// drops it, or a <c>Task&lt;Result&gt;</c> is turned into a plain <c>Task</c>. A dropped Result is a failure
/// nobody reads: <c>invoice.Issue(now); return Result.Success();</c> commits a refused transition and
/// answers 2xx.
/// </summary>
/// <remarks>
/// Matching is by type, not by method name, so every aggregate method, value-object factory, handler
/// and combinator is covered without a list, and an awaited <c>Task&lt;Result&gt;</c> is seen through the
/// <c>await</c>; a value carrying a result (an array, a sequence, a tuple, a task of one) counts too. The
/// types match by name and by a containing namespace ending in <c>SharedKernel.Results</c> (or
/// <c>UI.Common.Services</c>), which keeps working after the template renames the root namespace.
/// Reported: a call, an awaited task, a creation, <c>Wait()</c> on a task and a conditional access ending
/// in one of those, written as a statement (the implicit statement of a void expression body included);
/// an unawaited task where CS4014 is silent; a collection initializer's <c>Add</c>; and a computed
/// <c>Task&lt;Result&gt;</c> converted to <c>Task</c>. Not reported: an assignment (so <c>_ = …</c> is the
/// way to drop one on purpose), a return, an argument, NSubstitute's call check, an assertion that hands
/// back what it checked, the body of a delegate an exception assertion expects to throw, a call that
/// hands back a result something else holds, an expression tree, and generated code other than Razor's.
/// The facts in <c>ResultDiscardAnalyzerTests</c> are the specification; ADR-0030 lists the gaps.
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
        // Generated code is read only to reach a Razor component's @code block; IsInScope skips the rest.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeExpressionStatement, OperationKind.ExpressionStatement);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeDelegateCreation, OperationKind.DelegateCreation);
        context.RegisterOperationAction(AnalyzeCollectionInitializer, OperationKind.ObjectOrCollectionInitializer);
    }

    private static void AnalyzeExpressionStatement(OperationAnalysisContext context)
    {
        IOperation statementValue = ((IExpressionStatementOperation)context.Operation).Operation;
        if (DroppedValue(statementValue) is { Type: { } type } dropped
            && CarriesResult(type)
            && IsInScope(context)
            && !IsCompilersUnawaitedCall(context, dropped, type)
            && !IsSubstituteCallCheck(dropped)
            && !IsAssertion(dropped)
            && !HandsBackHeldValue(dropped)
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
            && IsInScope(context)
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
            && IsInScope(context)
            && !IsExpectedToThrow(creation))
        {
            Report(context, TaskRule, reference.Syntax, returned, delegateReturns);
        }
    }

    /// <summary>
    /// <c>new LineBook { "first line" }</c> calls <c>Add</c> for every element and throws each return
    /// away, so an <c>Add</c> that returns a result has every refusal dropped by the compiler itself.
    /// </summary>
    private static void AnalyzeCollectionInitializer(OperationAnalysisContext context)
    {
        foreach (IOperation initializer in ((IObjectOrCollectionInitializerOperation)context.Operation).Initializers)
        {
            if (initializer is IInvocationOperation { IsImplicit: true, Type: { } type } add && CarriesResult(type) && IsInScope(context))
            {
                Report(context, Rule, add.Syntax, type);
            }
        }
    }

    /// <summary>Hand-written code that runs: see <see cref="IsHandWritten"/> and <see cref="IsInExpressionTree"/>.</summary>
    private static bool IsInScope(OperationAnalysisContext context)
        => IsHandWritten(context) && !IsInExpressionTree(context.Operation);

    /// <summary>
    /// Inside a lambda converted to an expression tree (<c>Expression&lt;Action&lt;Invoice&gt;&gt; e = i =&gt;
    /// i.Void();</c>): the tree is data, never executed, so it computes no result.
    /// </summary>
    private static bool IsInExpressionTree(IOperation operation)
    {
        for (IOperation? enclosing = operation.Parent; enclosing is not null; enclosing = enclosing.Parent)
        {
            if (enclosing is IAnonymousFunctionOperation { Parent.Type: { } converted } && IsExpressionTreeType(converted))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>System.Linq.Expressions.Expression&lt;TDelegate&gt;</c>, through its base <c>LambdaExpression</c>.</summary>
    private static bool IsExpressionTreeType(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current is { Name: "LambdaExpression", ContainingNamespace: { Name: "Expressions", ContainingNamespace: { Name: "Linq", ContainingNamespace.Name: "System" } } })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Not generated, or generated by Razor from a component or view: its <c>@code</c> block is hand-written
    /// C# that reaches the compiler as the Razor source generator's output (<c>Pages/InvoiceDetail_razor.g.cs</c>).
    /// Every other generated file (EF migrations, other source generators, a <c>// &lt;auto-generated/&gt;</c>
    /// header, a <c>.g.cs</c> or <c>.Designer.cs</c> name) is skipped.
    /// </summary>
    private static bool IsHandWritten(OperationAnalysisContext context)
    {
        if (!context.IsGeneratedCode)
        {
            return true;
        }

        string path = context.Operation.Syntax.SyntaxTree.FilePath;
        return path.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase);
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
    /// A creation counts in every form whose type the compiler knows: <c>new X(dynamicArgument)</c> and
    /// <c>new T()</c> as well as a plain <c>new X()</c>. <c>task.Wait()</c> returns nothing (or a timeout
    /// flag) and leaves the task's result unread, so it drops the task it waited on.
    /// </summary>
    private static IOperation? DroppedValue(IOperation operation)
    {
        while (operation is IConditionalAccessOperation conditionalAccess)
        {
            operation = conditionalAccess.WhenNotNull;
        }

        if (operation is IInvocationOperation { TargetMethod: { Name: "Wait", ContainingType: { Arity: 0, Name: "Task" } owner }, Instance: { } waited }
            && IsSystemThreadingTasks(owner.ContainingNamespace))
        {
            return DroppedValue(waited);
        }

        return operation.Kind is OperationKind.Invocation
            or OperationKind.Await
            or OperationKind.ObjectCreation
            or OperationKind.DynamicObjectCreation
            or OperationKind.TypeParameterObjectCreation
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

    /// <summary>
    /// An assertion that hands back the value it checked, for chaining: a method of the Shouldly or xUnit
    /// libraries (<c>result.Error.ShouldBeOfType&lt;ValidationError&gt;()</c>, <c>ShouldNotBeNull()</c>,
    /// <c>Assert.IsType&lt;T&gt;</c>) or of a class marked <c>[ShouldlyMethods]</c>, as the tests' own
    /// <c>ShouldBeSuccess()</c> is. Checking the value is what the test does with it. An assertion whose
    /// checked value is a delegate (<c>Should.NotThrow(() =&gt; invoice.Issue(now))</c>) hands back what the
    /// delegate computed, checked only for not throwing, and stays reported.
    /// </summary>
    private static bool IsAssertion(IOperation dropped)
        => dropped is IInvocationOperation { TargetMethod: { } method }
           && method.Parameters.FirstOrDefault()?.Type.TypeKind != TypeKind.Delegate
           && (RootNamespaceName(method.ContainingNamespace) is "Shouldly" or "Xunit"
               || method.ContainingType.GetAttributes().Any(static attribute => attribute.AttributeClass is { Name: "ShouldlyMethodsAttribute" }));

    /// <summary>
    /// A call that returns a result something else already holds, so dropping the return loses nothing:
    /// <list type="bullet">
    /// <item><c>Tap</c> on a result held in a local, parameter or field returns that same result (the
    /// receiver computed on the spot, <c>invoice.Issue(now).Tap(…);</c>, is still a drop);</item>
    /// <item>a collection's own method typed by its element (<c>Stack.Pop</c>, <c>Queue.Dequeue</c>,
    /// <c>ConcurrentDictionary.GetOrAdd</c> and <c>AddOrUpdate</c>) returns an element it holds;</item>
    /// <item>a method returning the old value of a <c>ref</c> location (<c>Interlocked.Exchange</c>,
    /// <c>CompareExchange</c>) returns what that location held.</item>
    /// </list>
    /// </summary>
    private static bool HandsBackHeldValue(IOperation dropped)
    {
        if (dropped is IAwaitOperation awaited)
        {
            dropped = awaited.Operation;
        }

        if (dropped is not IInvocationOperation { TargetMethod: { } method } invocation)
        {
            return false;
        }

        if (method.OriginalDefinition.ReturnType is ITypeParameterSymbol returned)
        {
            if (returned.TypeParameterKind == TypeParameterKind.Type
                && !method.IsStatic
                && method.ContainingType.AllInterfaces.Any(static i => i.SpecialType == SpecialType.System_Collections_IEnumerable))
            {
                return true;
            }

            if (method.OriginalDefinition.Parameters.Any(parameter => parameter.RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter
                                                                        && SymbolEqualityComparer.Default.Equals(parameter.Type, returned)))
            {
                return true;
            }
        }

        return method.Name == "Tap"
               && method.ContainingType.ContainingNamespace is { Name: "Results", ContainingNamespace.Name: "SharedKernel" }
               && invocation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0)?.Value is { } receiver
               && IsHeld(receiver);
    }

    /// <summary>A reference to a value stored in a local, a parameter, a field or an array element.</summary>
    private static bool IsHeld(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value.Kind is OperationKind.LocalReference
            or OperationKind.ParameterReference
            or OperationKind.FieldReference
            or OperationKind.ArrayElementReference;
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
        { SpecialType: not SpecialType.None } => false,
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
            if (IsSharedKernelResult(current) || IsIdentityResult(current) || IsUiApiResult(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>Result</c>, <c>Result&lt;T&gt;</c> or <c>Error</c> (with <c>FieldError</c> and <c>ValidationError</c>,
    /// which derive from it) in a namespace ending in <c>SharedKernel.Results</c>. A dropped <c>Error</c> is a
    /// forgotten <c>return</c>: <c>if (invoice is null) { InvoiceErrors.NotFound(id); }</c> falls through to
    /// success.
    /// </summary>
    private static bool IsSharedKernelResult(ITypeSymbol type)
        => type.Name is "Result" or "Error"
           && type.ContainingNamespace is { Name: "Results", ContainingNamespace.Name: "SharedKernel" };

    /// <summary>
    /// The UI's <c>ApiResult&lt;T&gt;</c>, in a namespace ending in <c>UI.Common.Services</c>: a dropped
    /// <c>await InvoiceApi.PayAsync(id)</c> shows a refused payment as done.
    /// </summary>
    private static bool IsUiApiResult(ITypeSymbol type)
        => type.Name == "ApiResult"
           && type.ContainingNamespace is { Name: "Services", ContainingNamespace: { Name: "Common", ContainingNamespace.Name: "UI" } };

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
