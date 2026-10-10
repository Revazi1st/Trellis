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
        }
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

            IEnumerable<PropertyInfo> publicSetters = entity
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true } && !interceptorProperties.Contains(p.Name));

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

    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

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
