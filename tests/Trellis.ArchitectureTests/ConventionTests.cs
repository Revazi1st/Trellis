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

    /// <summary>
    /// The files under the throw scan's folders that may throw, keyed by repository-relative path, each with
    /// its reason. An entry is a decision that the failure is a programmer error no caller can act on, never
    /// an expected outcome; a stale one (the file gone, or no longer throwing) fails the scan.
    /// </summary>
    private static readonly Dictionary<string, string> ThrowAllowlist = new(StringComparer.Ordinal)
    {
        ["src/Core/Trellis.Domain/Common/Money.cs"] =
            "cross-currency arithmetic and a negative difference are programmer errors, not expected failures (the file header)",
    };

    /// <summary>
    /// The files under <c>src/</c> and <c>tools/</c> that may read the machine's clock or mint a random
    /// <see cref="Guid"/>, keyed by repository-relative path, each with its reason. Empty on purpose: the
    /// time is the registered <see cref="TimeProvider"/>'s (ADR-0009), an id its type's <c>New()</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ClockAndIdAllowlist = new(StringComparer.Ordinal);

    /// <summary>
    /// The files under <c>src/</c> that may build a <c>PagedResponse</c>, keyed by repository-relative path, each
    /// with its reason: the shared mapping every list calls, and nothing per feature.
    /// </summary>
    private static readonly Dictionary<string, string> PagedResponseAllowlist = new(StringComparer.Ordinal)
    {
        ["src/Presentation/Api/Trellis.Api.Common/Http/PagedResponseMappings.cs"] =
            "the one PagedList-to-envelope mapping; every list endpoint calls ToPagedResponse(map)",
    };

    /// <summary>The test files that may name a sleep, keyed by repository-relative path, each with its reason.</summary>
    private static readonly Dictionary<string, string> SleepAllowlist = new(StringComparer.Ordinal)
    {
        ["tests/Trellis.ArchitectureTests/Analyzers/ResultDiscardAnalyzerTests.cs"] = "compiled snippets, never run",
    };

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
    public void Aggregates_are_created_through_a_static_Create_returning_Result()
    {
        // The factory is where an aggregate's invariants are checked (domain.md, Construction): a public
        // constructor builds one that skips them, and a factory that throws or hands back the bare
        // aggregate turns a refused input into an exception instead of an Error the caller can show.
        List<Type> aggregates = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && DerivesFromGeneric(t, typeof(AggregateRoot<>)))];

        aggregates.ShouldNotBeEmpty();   // Invoice anchors the convention

        aggregates.SelectMany(AggregateFactoryViolations).ShouldBeEmpty(
            "an aggregate has a static Create(...) returning Result<TAggregate> (internal when only its parent aggregate creates it), "
            + "no other public static method that hands back the bare aggregate, and a private constructor for EF (domain.md)");
    }

    [Fact]
    public void The_aggregate_factory_rule_refuses_an_aggregate_with_a_public_constructor_and_no_Create()
        => AggregateFactoryViolations(typeof(PlantedViolations.AggregateWithoutFactory))
            .ShouldBe(
            [
                "AggregateWithoutFactory has no static Create(...) returning Result<AggregateWithoutFactory>",
                "AggregateWithoutFactory(PlantedId) is a public constructor",
            ]);

    [Fact]
    public void The_aggregate_factory_rule_refuses_a_second_way_in_that_hands_back_the_bare_aggregate()
        => AggregateFactoryViolations(typeof(PlantedViolations.AggregateWithBareFactory))
            .ShouldBe(["AggregateWithBareFactory.FromSnapshot hands back the bare aggregate"]);

    [Fact]
    public void The_aggregate_factory_rule_accepts_an_internal_Create_that_a_parent_aggregate_calls()
        => AggregateFactoryViolations(typeof(PlantedViolations.AggregateCreatedByItsParent)).ShouldBeEmpty();

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

    [Fact]
    public void The_domain_the_use_cases_and_the_endpoints_return_a_Result_and_never_throw()
    {
        // An expected failure is a Result (ADR-0004): an aggregate returns an Error from its *Errors class,
        // a handler returns the aggregate's or the port's, an endpoint maps it to a problem. Domain-event
        // handlers report failure by throwing (ADR-0023, the outbox's retry signal), so Features/*/EventHandlers/
        // is outside the scan; so are the ports' adapters in Infrastructure and Persistence.
        List<SourceFile> scanned = [.. RepositorySource.CSharpFiles("src").Where(static file => ThrowScanScope().IsMatch(file.RelativePath))];

        scanned.ShouldNotBeEmpty("the throw scan found no file in the Domain, Application's Features/ or the Api's Features/; is the repository root right?");
        SourceScan.StaleEntries(scanned, ThrowAllowlist, ThrowsIn).ShouldBeEmpty();
        SourceScan.Violations(scanned, ThrowAllowlist, ThrowsIn).ShouldBeEmpty(
            "return an Error from the aggregate's *Errors class (or the port's) and let the endpoint map it with CustomResults.Problem; "
            + "a programmer error no caller can act on goes on ThrowAllowlist with its reason");
    }

    /// <summary>
    /// Positive control for the throw scan, which expects zero hits outside its allowlist: a throw
    /// expression and a rethrow are found; a word that only contains the keyword, Shouldly's assertion and
    /// the guard helpers are not. The scan reads <c>src/</c> only, so the rows are written out whole.
    /// </summary>
    [Theory]
    [InlineData("?? throw new InvalidOperationException()", true)]
    [InlineData("throw;", true)]
    [InlineData("int thrower = 1;", false)]
    [InlineData("Should.Throw<Exception>(() => x)", false)]
    [InlineData("ArgumentNullException.ThrowIfNull(currency);", false)]
    public void The_throw_scan_flags_a_throw_and_nothing_that_only_looks_like_one(string code, bool flagged)
        => ThrowsIn(new SourceFile("Planted.cs", code)).Any().ShouldBe(flagged);

    [Fact]
    public void Time_comes_from_the_TimeProvider_and_ids_from_their_type()
    {
        // The clock is the registered TimeProvider (ADR-0009), so a test stands at any instant it names
        // (FakeTimeProvider, ApiFactory.Time, the UI harness's manual clock) and a deadline computed in one
        // place agrees with one computed in another. An id is its type's New(): a version-7 Guid, ordered by
        // time, where a random one scatters the primary key's index (domain.md, Strongly-typed IDs). A text
        // scan cannot follow `using static System.DateTime;` or a type alias (`using Clock = System.DateTime;`),
        // so neither is written (application.md); a string literal that names the call is flagged, literals
        // being kept.
        List<SourceFile> scanned = [.. RepositorySource.CSharpFiles("src", "tools"), .. RazorFiles("src", "tools")];

        scanned.ShouldNotBeEmpty("the clock scan found no file under src/ or tools/; is the repository root right?");
        SourceScan.StaleEntries(scanned, ClockAndIdAllowlist, ClockOrIdReadsIn).ShouldBeEmpty();
        SourceScan.Violations(scanned, ClockAndIdAllowlist, ClockOrIdReadsIn).ShouldBeEmpty(
            "inject TimeProvider and call GetUtcNow() (UI.Common registers one for the UI), and mint an id with its type's New(); "
            + "a deliberate exception goes on ClockAndIdAllowlist with its reason");
    }

    /// <summary>
    /// Positive control for the clock and id scan, which expects zero hits: each member that reads the
    /// machine's clock (today's date included) or mints a random Guid is found, called or as a method group;
    /// the TimeProvider, an id type's New(), a version-7 Guid and the other members of the same types are not.
    /// The scan reads <c>src/</c> and <c>tools/</c> only, so the rows are written out whole.
    /// </summary>
    [Theory]
    [InlineData("var now = DateTime.UtcNow;", true)]
    [InlineData("var now = DateTime.Now;", true)]
    [InlineData("var today = DateTime.Today;", true)]
    [InlineData("var now = DateTimeOffset.UtcNow;", true)]
    [InlineData("var now = System.DateTimeOffset.Now;", true)]
    [InlineData("var id = Guid.NewGuid();", true)]
    [InlineData("Func<Guid> mint = Guid.NewGuid;", true)]
    [InlineData("<p>@DateTime.Now.Year</p>", true)]
    [InlineData("<p>@DateTime.Today</p>", true)]
    [InlineData("var now = time.GetUtcNow();", false)]
    [InlineData("var id = OrderId.New();", false)]
    [InlineData("var key = Guid.CreateVersion7();", false)]
    [InlineData("var epoch = DateTimeOffset.UnixEpoch;", false)]
    [InlineData("var none = Guid.Empty;", false)]
    public void The_clock_scan_flags_the_machine_clock_and_a_random_Guid(string code, bool flagged)
        => ClockOrIdReadsIn(new SourceFile("Planted.cs", code)).Any().ShouldBe(flagged);

    [Fact]
    public void The_paged_envelope_is_built_only_by_the_shared_mapping()
    {
        // PagedResponse takes four ints and two bools by position, so a hand-written copy that swaps
        // TotalCount and TotalPages compiles and passes a test that happens to use equal numbers. A list
        // endpoint maps with Api.Common's ToPagedResponse(map) instead (api.md, List endpoints); a slice's
        // own mapping maps the item, never the envelope.
        List<SourceFile> scanned = [.. RepositorySource.CSharpFiles("src")];

        scanned.ShouldNotBeEmpty("the paged-envelope scan found no file under src/; is the repository root right?");
        SourceScan.StaleEntries(scanned, PagedResponseAllowlist, PagedResponseBuildsIn).ShouldBeEmpty();
        SourceScan.Violations(scanned, PagedResponseAllowlist, PagedResponseBuildsIn).ShouldBeEmpty(
            "return result.Value.ToPagedResponse(item => item.ToDto()) (Trellis.Api.Common.Http.PagedResponseMappings) "
            + "and map only the item in the feature's ContractMappings");
    }

    /// <summary>
    /// Positive control for the paged-envelope scan, which expects zero hits outside its allowlist: a
    /// constructor call, qualified or not, and a method declared to return the envelope (the dry run's
    /// per-feature copy, which builds it with a target-typed <c>new(…)</c>) are found; the envelope as a
    /// type argument of an endpoint's or a client's return type, a call to the shared mapping, a field and
    /// the record's own declaration are not. The scan reads <c>src/</c> only, so the rows are written out whole.
    /// </summary>
    [Theory]
    [InlineData("return new PagedResponse<CustomerDto>([], 1, 20, 0, 0, false, false);", true)]
    [InlineData("=> new Trellis.Api.Contracts.Common.PagedResponse<TOut>(items, 1, 20, 0, 0, false, false);", true)]
    [InlineData("public static PagedResponse<CustomerDto> ToPagedResponse(this PagedList<CustomerResponse> page)", true)]
    [InlineData("public static Trellis.Api.Contracts.Common.PagedResponse<TOut> ToPagedResponse<TIn, TOut>(", true)]
    [InlineData("private static async Task<Results<Ok<PagedResponse<InvoiceDto>>, ProblemHttpResult>> Handle(", false)]
    [InlineData("Task<ApiResult<PagedResponse<InvoiceDto>>> ListAsync(", false)]
    [InlineData("? TypedResults.Ok(result.Value.ToPagedResponse(static i => i.ToDto()))", false)]
    [InlineData("private PagedResponse<InvoiceDto>? _page;", false)]
    [InlineData("public sealed record PagedResponse<T>(", false)]
    public void The_paged_envelope_scan_flags_a_copy_and_not_a_use(string code, bool flagged)
        => PagedResponseBuildsIn(new SourceFile("Planted.cs", code)).Any().ShouldBe(flagged);

    [Fact]
    public void Tests_move_time_and_never_sleep()
    {
        // A test that waits on the machine's clock is slow when it passes and flaky when the machine is busy
        // (tests.md: move time, never sleep). Time moves with FakeTimeProvider.Advance, ApiFactory.Time or the
        // UI harness's clock; a wait for another thread is a TaskCompletionSource. A delay of Timeout.Infinite
        // is a wait for cancellation, not a sleep, and passes.
        List<SourceFile> scanned = [.. RepositorySource.CSharpFiles("tests")];

        scanned.ShouldNotBeEmpty("the sleep scan found no file under tests/; is the repository root right?");
        SourceScan.StaleEntries(scanned, SleepAllowlist, SleepsIn).ShouldBeEmpty();
        SourceScan.Violations(scanned, SleepAllowlist, SleepsIn).ShouldBeEmpty(
            "advance the test's clock (FakeTimeProvider, ApiFactory.Time, UiHarness.Clock) or await the signal itself; "
            + "a file whose text only names the call goes on SleepAllowlist with its reason");
    }

    /// <summary>
    /// Positive control for the sleep scan, which expects zero hits outside its allowlist: a thread sleep and
    /// a timed delay are found, however the delay is written; a delay of <c>Timeout.Infinite</c> (a wait for
    /// cancellation) is not. The scan reads <c>tests/</c>, this file among them, so each call is assembled
    /// from two literals: written out whole, it would trip the very scan it tests.
    /// </summary>
    [Theory]
    [InlineData("await Task", ".Delay(100);", true)]
    [InlineData("await Task", ".Delay(TimeSpan.FromSeconds(1), cancellationToken);", true)]
    [InlineData("Thread", ".Sleep(1000);", true)]
    [InlineData("System.Threading.Thread", ".Sleep(TimeSpan.FromMilliseconds(50));", true)]
    [InlineData("await Task", ".Delay(Timeout.Infinite, cancellationToken);", false)]
    [InlineData("await Task", ".Delay(Timeout.InfiniteTimeSpan, cancellationToken);", false)]
    [InlineData("await Task", ".Delay( Timeout.Infinite, cancellationToken);", false)]
    [InlineData("await Task", ".Yield();", false)]
    public void The_sleep_scan_flags_a_wait_on_the_clock_and_not_a_wait_for_cancellation(string receiver, string call, bool flagged)
        => SleepsIn(new SourceFile("Planted.cs", receiver + call)).Any().ShouldBe(flagged);

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

    /// <summary>
    /// What the aggregate factory rule refuses in <paramref name="aggregate"/>: no static <c>Create</c> returning
    /// <c>Result&lt;TAggregate&gt;</c> (public, or internal for an aggregate only its parent creates; Application
    /// reaches that one only through the parent), each public static method that hands back the bare aggregate
    /// or a task of it, and each public constructor.
    /// </summary>
    private static IEnumerable<string> AggregateFactoryViolations(Type aggregate)
    {
        Type result = typeof(Result<>).MakeGenericType(aggregate);
        Type[] bare = [aggregate, typeof(Task<>).MakeGenericType(aggregate)];
        MethodInfo[] statics = aggregate.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

        if (!statics.Any(m => m.Name == "Create" && m.ReturnType == result))
        {
            yield return $"{aggregate.Name} has no static Create(...) returning Result<{aggregate.Name}>";
        }

        foreach (MethodInfo method in statics.Where(m => m.IsPublic && bare.Contains(m.ReturnType)))
        {
            yield return $"{aggregate.Name}.{method.Name} hands back the bare aggregate";
        }

        foreach (ConstructorInfo constructor in aggregate.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return $"{aggregate.Name}({string.Join(", ", constructor.GetParameters().Select(static p => DisplayName(p.ParameterType)))}) is a public constructor";
        }
    }

    /// <summary>Every <c>*.razor</c> file under the given top-level directories, its markup comments stripped.</summary>
    private static IEnumerable<SourceFile> RazorFiles(params string[] topLevelDirectories)
        => RepositorySource.RazorAndCssFiles(topLevelDirectories)
            .Where(static file => file.RelativePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<int> ThrowsIn(SourceFile file) => Throw().Matches(file.Code).Select(static m => m.Index);

    private static IEnumerable<int> ClockOrIdReadsIn(SourceFile file) => ClockOrRandomId().Matches(file.Code).Select(static m => m.Index);

    private static IEnumerable<int> SleepsIn(SourceFile file) => Sleep().Matches(file.Code).Select(static m => m.Index);

    private static IEnumerable<int> PagedResponseBuildsIn(SourceFile file)
        => PagedResponseConstruction().Matches(file.Code).Select(static m => m.Index);

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

    /// <summary>
    /// The throw scan's folders, by repository-relative path: the Domain, Application's <c>Features/</c>
    /// outside any <c>EventHandlers/</c> folder, and the Api's <c>Features/</c>.
    /// </summary>
    [GeneratedRegex(@"^src/(?:Core/[^/]+\.Domain/|Core/[^/]+\.Application/Features/(?!(?:[^/]+/)*EventHandlers/)|Presentation/Api/[^/]+\.Api/Features/)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThrowScanScope();

    /// <summary>The keyword, case-sensitive: <c>ThrowIfNull</c> and Shouldly's <c>Should.Throw</c> are other words.</summary>
    [GeneratedRegex(@"\bthrow\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Throw();

    /// <summary>A machine-clock member (<c>DateTime.Today</c> too) or <c>Guid.NewGuid</c>, called or as a method group.</summary>
    [GeneratedRegex(@"\bDateTime\.(?:UtcNow|Now|Today)\b|\bDateTimeOffset\.(?:UtcNow|Now)\b|\bGuid\.NewGuid\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClockOrRandomId();

    /// <summary>
    /// A thread sleep, or a delay whose first argument is not <c>Timeout.Infinite</c> (or <c>InfiniteTimeSpan</c>).
    /// The whitespace before the argument is matched atomically, so backtracking cannot slip the lookahead
    /// past it.
    /// </summary>
    [GeneratedRegex(@"\bThread\.Sleep\(|\bTask\.Delay\((?>\s*)(?!Timeout\.Infinite)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sleep();

    /// <summary>
    /// <c>new PagedResponse&lt;</c>, qualified or not, or a method declared to return a <c>PagedResponse&lt;…&gt;</c>:
    /// the type at the start of a declaration (not a type argument, which follows <c>&lt;</c> or <c>,</c>), its
    /// arguments (one level of nesting), then a member name and its parameter or type-parameter list.
    /// </summary>
    [GeneratedRegex(@"\bnew\s+(?:[\w.]+\.)?PagedResponse\s*<|(?<![<,]\s*)(?<![\w.])(?:[\w.]+\.)?PagedResponse<[^<>]*(?:<[^<>]*>[^<>]*)*>\s+\w+\s*[<(]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PagedResponseConstruction();
}
