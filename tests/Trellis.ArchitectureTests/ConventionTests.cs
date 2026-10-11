using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Shouldly;
using Trellis.Api.Common.Endpoints;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The conventions the layer rules state as mandatory, enforced instead of asserted in prose:
/// uniform slices are what keep the template navigable for humans and agents alike.
/// </summary>
public sealed partial class ConventionTests
{
    /// <summary>
    /// The only places allowed to call the bare <c>IgnoreQueryFilters()</c> overload, keyed by
    /// repository-relative path, each with the reason it must see every tenant's and every deleted row.
    /// Empty on purpose: today no code path needs a wholesale bypass — the soft-delete rescue names its
    /// filter (<c>IgnoreQueryFilters(["SoftDeleted"])</c>), and the janitor's bookkeeping tables carry no
    /// query filters at all. Adding an entry is a security decision: say why in the value.
    /// </summary>
    private static readonly Dictionary<string, string> BareIgnoreQueryFiltersAllowlist = new(StringComparer.Ordinal);

    /// <summary>The open handler contracts Application's assembly scan registers (ADR-0003).</summary>
    private static readonly Type[] HandlerContracts =
        [typeof(ICommandHandler<>), typeof(ICommandHandler<,>), typeof(IQueryHandler<,>), typeof(IDomainEventHandler<>)];

    /// <summary>What a handler never injects: another command's pipeline, or the commit itself.</summary>
    private static readonly Type[] UseCaseCompositionTypes = [typeof(ICommandHandler<>), typeof(ICommandHandler<,>), typeof(IUnitOfWork)];

    [Fact]
    public void Command_and_query_handlers_are_sealed()
        => Types.InAssembly(Architecture.ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(ICommandHandler<>))
            .Or().ImplementInterface(typeof(ICommandHandler<,>))
            .Or().ImplementInterface(typeof(IQueryHandler<,>))
            .Should()
            .BeSealed()
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Command_and_query_handlers_are_not_public()
        => Types.InAssembly(Architecture.ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(ICommandHandler<>))
            .Or().ImplementInterface(typeof(ICommandHandler<,>))
            .Or().ImplementInterface(typeof(IQueryHandler<,>))
            .Should()
            .NotBePublic()
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Handlers_inject_neither_another_command_handler_nor_the_unit_of_work()
    {
        // One command, one commit (application.md). An injected ICommandHandler<B> is B's whole decorated
        // chain, and B's UnitOfWork stage commits the shared request-scoped DbContext halfway through A: A's
        // staged changes are saved before A has decided, and a conflict in B clears them. An injected
        // IUnitOfWork is the same hole without the detour. No non-empty guard on purpose: a project that
        // toggles or remove-feature leave with few handlers must still pass.
        List<string> violations = [.. ImplementationsOf(Architecture.ApplicationAssembly, HandlerContracts)
            .SelectMany(UseCaseCompositionIn)];

        violations.ShouldBeEmpty(
            "share the logic through the aggregate, a domain service or a port, never another use case's handler; "
            + "the UnitOfWork decorator commits once per command, so no handler takes IUnitOfWork");
    }

    [Fact]
    public void Handlers_live_only_in_Application()
    {
        // AddApplication's assembly scan registers Application's handlers, and Scrutor decorates only the
        // descriptors present when it runs. A handler declared anywhere else is never registered, or, added
        // by hand later, runs without tracing, logging, validation or a commit, and nothing reports it. The
        // SharedKernel's pipeline stages implement the contracts too: they wrap a handler, they are not one.
        List<string> strays = [.. HandlersOutside(
        [
            Architecture.SharedKernelAssembly,
            Architecture.DomainAssembly,
            Architecture.InfrastructureAssembly,
            Architecture.PersistenceAssembly,
            Architecture.ApiContractsAssembly,
            Architecture.ApiCommonAssembly,
            Architecture.ApiAssembly,
        ])];

        strays.ShouldBeEmpty("a command, query or domain-event handler lives in Application's Features/, where the scan and the pipeline find it");
    }

    [Fact]
    public void The_location_rule_refuses_a_handler_outside_Application()
        => HandlersOutside([typeof(PlantedViolations).Assembly])
            .ShouldContain(typeof(PlantedViolations.HandlerCallingOtherUseCases).FullName!, StringComparer.Ordinal);

    [Fact]
    public void The_composition_rule_refuses_a_handler_calling_other_use_cases()
        => UseCaseCompositionIn(typeof(PlantedViolations.HandlerCallingOtherUseCases))
            .Count()
            .ShouldBe(3, "the planted handler injects a command handler, a Func of one and IUnitOfWork");

    [Fact]
    public void Endpoints_are_internal_sealed_one_class_per_use_case()
        => Types.InAssembly(Architecture.ApiAssembly)
            .That()
            .ImplementInterface(typeof(IEndpoint))
            .Should()
            .BeSealed()
            .And().NotBePublic()
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Value_objects_are_sealed_records_with_a_Create_factory_returning_Result()
    {
        List<Type> valueObjects = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false, IsNested: false }
                               && IsRecord(t)
                               && !typeof(IDomainEvent).IsAssignableFrom(t))];

        valueObjects.ShouldNotBeEmpty();   // Money and EmailAddress anchor the convention

        foreach (Type valueObject in valueObjects)
        {
            valueObject.IsSealed.ShouldBeTrue($"{valueObject.Name} must be a sealed record (.claude/rules/domain.md)");

            MethodInfo? create = valueObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(static m => m.Name == "Create");
            create.ShouldNotBeNull($"{valueObject.Name} needs a static Create(...) factory");
            create.ReturnType.IsGenericType.ShouldBeTrue($"{valueObject.Name}.Create must return Result<T>");
            create.ReturnType.GetGenericTypeDefinition().ShouldBe(typeof(Result<>),
                $"{valueObject.Name}.Create must return Result<T>, not throw");

            // Create is the only way in: a public constructor (a positional record's, say) or a public
            // set/init accessor (which `with` and object initialisers use) builds an unvalidated value.
            valueObject.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .ShouldBeEmpty($"{valueObject.Name} must be created through its static Create factory, not a public constructor");

            PublicSettersOf(valueObject).ShouldBeEmpty(
                $"{valueObject.Name} exposes public set or init accessors — `with` and initialisers would skip Create's checks");
        }
    }

    [Fact]
    public void The_value_object_rule_refuses_a_positional_record()
    {
        Type planted = typeof(PlantedViolations.PositionalValueObject);

        planted.GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldNotBeEmpty();
        PublicSettersOf(planted).ShouldNotBeEmpty("a positional record's members carry public init accessors");
    }

    [Fact]
    public void Domain_events_are_sealed_records()
    {
        List<Type> events = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))];

        events.ShouldNotBeEmpty();
        events.ShouldAllBe(static e => e.IsSealed, "domain events are sealed records");
        events.ShouldAllBe(e => IsRecord(e), "domain events are records (value equality is what handlers and tests rely on)");
    }

    [Fact]
    public void Domain_event_handlers_carry_a_unique_stable_name()
    {
        // The outbox credits a handler under this name; a retry skips credited handlers. Derived from the
        // CLR name, a rename would re-run a handler that already sent its email (audit 4.5) — so the name
        // is explicit, lowercase-dotted like the event wire names, and unique (a shared one would make the
        // second handler look credited and skip it forever).
        List<(Type Handler, string? Name)> handlers = [.. Architecture.ApplicationAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false }
                               && t.GetInterfaces().Any(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            .Select(static t => (t, t.GetCustomAttribute<DomainEventHandlerNameAttribute>()?.Name))];

        foreach ((Type handler, string? name) in handlers)
        {
            name.ShouldNotBeNull($"{handler.Name} needs [DomainEventHandlerName(\"feature.event.reaction\")] — outbox credits must survive a rename");
            StableName().IsMatch(name).ShouldBeTrue($"{handler.Name}'s handler name '{name}' must be lowercase and dotted, like 'invoices.paid.send-receipt'");
        }

        handlers.GroupBy(static h => h.Name, StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => $"{g.Key}: {string.Join(", ", g.Select(static h => h.Handler.Name))}")
            .ShouldBeEmpty("every domain-event handler needs its own name");
    }

    [Fact]
    public void Aggregates_and_entities_have_no_public_constructors_and_no_public_setters()
    {
        // Interceptor-stamped bookkeeping is the sanctioned exception: those setters exist for the
        // AuditingInterceptor/SoftDeleteInterceptor, never for callers.
        HashSet<string> interceptorProperties = [.. typeof(IAuditable).GetProperties()
            .Concat(typeof(ISoftDeletable).GetProperties())
            .Select(static p => p.Name)];

        List<Type> entities = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && DerivesFromGeneric(t, typeof(Entity<>)))];

        entities.ShouldNotBeEmpty();

        foreach (Type entity in entities)
        {
            entity.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .ShouldBeEmpty($"{entity.Name} must be created through its static Create factory, not a public constructor");

            IEnumerable<PropertyInfo> publicSetters = PublicSettersOf(entity)
                .Where(p => !interceptorProperties.Contains(p.Name));

            publicSetters.ShouldBeEmpty(
                $"{entity.Name} exposes public setters — mutate through intention-revealing methods instead");
        }
    }

    [Fact]
    public void Cancellation_filters_key_on_the_token_not_only_the_exception_type()
    {
        // A downstream HttpClient timeout is an OperationCanceledException too. A filter keyed on the
        // TYPE alone lets that fault escape the outbox (no attempt counted, the host stops) and the
        // decorators (no use-case log line). The rule: "e is not OperationCanceledException ||
        // !ourToken.IsCancellationRequested", keyed on the host/request token, never a linked one — so the
        // token must be one of the sanctioned names, which only ever hold the token handed in by the host
        // or the request.
        List<(SourceFile File, Match Filter)> typeFilters = [.. RepositorySource.CSharpFiles("src", "tools")
            .SelectMany(static file => CatchFilter().Matches(file.Code)
                .Where(static m => m.Value.Contains("is not OperationCanceledException", StringComparison.Ordinal))
                .Select(m => (file, m)))];

        typeFilters.ShouldNotBeEmpty("the scan found no cancellation filter at all — is the repository root right?");
        typeFilters
            .Where(static f => !TokenKeyedCancellationFilter().IsMatch(f.Filter.Value))
            .Select(static f => $"{f.File.RelativePath}:{f.File.LineOf(f.Filter.Index)}: {f.Filter.Value}")
            .ShouldBeEmpty(
                "write the filter as `when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)` " +
                "(stoppingToken in a background service), keyed on the token the host or the request handed in. " +
                "A linked token such as dispatch.Token is refused: it also fires on a budget or timeout, so the filter " +
                "would let a retryable failure escape as if the host were stopping");
    }

    [Fact]
    public void Query_filters_are_switched_off_by_name_never_wholesale()
    {
        // The bare overload drops EVERY named filter: reaching for deleted rows also reads every
        // tenant's rows under --use-multitenancy, silently. Name the filter instead (ADR-0022).
        List<string> violations = [.. RepositorySource.CSharpFiles("src", "tools", "tests")
            .Where(static file => !BareIgnoreQueryFiltersAllowlist.ContainsKey(file.RelativePath))
            .SelectMany(static file => BareIgnoreQueryFilters().Matches(file.Code)
                .Select(m => $"{file.RelativePath}:{file.LineOf(m.Index)}"))];

        violations.ShouldBeEmpty(
            "call IgnoreQueryFilters([\"SoftDeleted\"]) (or [\"Tenant\"]) by name; a deliberate wholesale bypass goes on the allowlist with its reason");
    }

    /// <summary>
    /// Positive control for a scan that expects zero hits, whose pass cannot otherwise tell a clean codebase
    /// from a regex that matches nothing. The call is assembled here: written out whole, it would trip the
    /// very scan it tests, which reads <c>tests/</c> and keeps string literals.
    /// </summary>
    [Theory]
    [InlineData("()", true)]
    [InlineData("( )", true)]
    [InlineData("(\n)", true)]
    [InlineData("([\"SoftDeleted\"])", false)]
    [InlineData("([\"Tenant\"])", false)]
    public void The_bare_query_filter_scan_tells_a_bare_call_from_a_named_one(string arguments, bool flagged)
        => BareIgnoreQueryFilters().IsMatch("context.Orders.IgnoreQueryFilters" + arguments).ShouldBe(flagged);

    /// <summary>
    /// Positive control for the cancellation-filter scan, which also expects zero violations: the filter is
    /// found, and only the host or request token widens it, never a type-only check or a linked token.
    /// </summary>
    [Theory]
    [InlineData("when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)", true)]
    [InlineData("when (exception is not OperationCanceledException || ! stoppingToken.IsCancellationRequested && Retryable(exception))", true)]
    [InlineData("when (e is not OperationCanceledException)", false)]
    [InlineData("when (e is not OperationCanceledException || !dispatch.Token.IsCancellationRequested)", false)]
    [InlineData("when (e is not OperationCanceledException || !cancellationTokenSource.Token.IsCancellationRequested)", false)]
    public void The_cancellation_filter_scan_accepts_only_the_host_or_request_token(string filter, bool accepted)
    {
        Match found = CatchFilter().Match($"try {{ }} catch (Exception e) {filter} {{ }}");

        found.Value.ShouldBe(filter);
        TokenKeyedCancellationFilter().IsMatch(found.Value).ShouldBe(accepted);
    }

    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    /// <summary>The public instance properties with a public <c>set</c> or <c>init</c> accessor (to reflection an init accessor is a setter).</summary>
    private static IEnumerable<PropertyInfo> PublicSettersOf(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.SetMethod is { IsPublic: true });

    /// <summary>The concrete classes in <paramref name="assembly"/> implementing any of the open generic <paramref name="contracts"/>.</summary>
    private static IEnumerable<Type> ImplementationsOf(Assembly assembly, Type[] contracts)
        => assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && type.GetInterfaces().Any(i => i.IsGenericType && contracts.Contains(i.GetGenericTypeDefinition())));

    /// <summary>The handler implementations in <paramref name="assemblies"/>, the SharedKernel's pipeline stages excepted.</summary>
    private static IEnumerable<string> HandlersOutside(Assembly[] assemblies)
        => assemblies
            .SelectMany(static assembly => ImplementationsOf(assembly, HandlerContracts))
            .Where(static type => !IsPipelineStage(type))
            .Select(static type => type.FullName!);

    /// <summary>A decorator in the SharedKernel: an open generic whose constructor takes the contract it implements.</summary>
    private static bool IsPipelineStage(Type type)
    {
        if (!type.IsGenericTypeDefinition || type.Assembly != Architecture.SharedKernelAssembly)
        {
            return false;
        }

        HashSet<Type> implemented = [.. type.GetInterfaces().Where(static i => i.IsGenericType).Select(static i => i.GetGenericTypeDefinition())];
        return type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(static constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType.IsGenericType && implemented.Contains(parameter.ParameterType.GetGenericTypeDefinition()));
    }

    /// <summary>Each constructor parameter of <paramref name="handler"/> that reaches another use case or the commit.</summary>
    private static IEnumerable<string> UseCaseCompositionIn(Type handler)
        => handler.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(static constructor => constructor.GetParameters())
            .Where(static parameter => ReachesUseCaseComposition(parameter.ParameterType))
            .Select(parameter => $"{handler.FullName}({DisplayName(parameter.ParameterType)} {parameter.Name})");

    /// <summary>The type itself or anything it wraps: a Lazy, a Func or an IEnumerable resolves the same chain.</summary>
    private static bool ReachesUseCaseComposition(Type type)
        => UseCaseCompositionTypes.Contains(type.IsGenericType ? type.GetGenericTypeDefinition() : type)
           || (type.IsGenericType && type.GetGenericArguments().Any(ReachesUseCaseComposition))
           || (type.GetElementType() is { } element && ReachesUseCaseComposition(element));

    /// <summary><c>Func&lt;ICommandHandler&lt;X&gt;&gt;</c> rather than <c>Func`1</c>, for failure messages.</summary>
    private static string DisplayName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        int arity = type.Name.IndexOf('`', StringComparison.Ordinal);
        return $"{(arity < 0 ? type.Name : type.Name[..arity])}<{string.Join(", ", type.GetGenericArguments().Select(DisplayName))}>";
    }

    private static bool DerivesFromGeneric(Type type, Type openGeneric)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGeneric)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A catch filter <c>when ( … )</c>, balanced over nested parentheses.</summary>
    [GeneratedRegex(@"\bwhen\s*\((?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!))\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CatchFilter();

    /// <summary>The type check widened by the host/request token — by name: <c>cancellationToken</c> or <c>stoppingToken</c>, nothing linked.</summary>
    [GeneratedRegex(@"is not OperationCanceledException\s*\|\|\s*!\s*(?:cancellationToken|stoppingToken)\.IsCancellationRequested\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenKeyedCancellationFilter();

    [GeneratedRegex(@"\.IgnoreQueryFilters\(\s*\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareIgnoreQueryFilters();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(?:\.[a-z0-9-]+)+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StableName();
}
