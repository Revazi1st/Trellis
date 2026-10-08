using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.GetInvoice;
using Trellis.Application.UnitTests.Invoices;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests;

/// <summary>
/// Exercises the real DI container: AddApplication() wiring, Scrutor scanning, and the shape and
/// behavior of the decorator pipeline (Logging → Tracing → Validation → UnitOfWork → handler) end-to-end.
/// </summary>
public sealed class DecoratorPipelineTests : IDisposable
{
    private readonly PipelineHarness _pipeline = new();

    public void Dispose() => _pipeline.Dispose();

    [Fact]
    public async Task Valid_command_reaches_handler_and_commits_unit_of_work()
    {
        var handler = _pipeline.Resolve<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();

        var result = await handler.Handle(
            new CreateInvoiceCommand("ACME", "a@b.co", "USD"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _pipeline.Invoices.ReceivedWithAnyArgs(1).Add(default!);
        await _pipeline.UnitOfWork.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_command_short_circuits_before_handler_and_unit_of_work()
    {
        var handler = _pipeline.Resolve<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();

        var result = await handler.Handle(
            new CreateInvoiceCommand(CustomerName: "", CustomerEmail: "", Currency: "USD"),
            TestContext.Current.CancellationToken);

        var validationError = result.Error.ShouldBeOfType<ValidationError>();
        validationError.Errors.Count.ShouldBe(2);
        validationError.Errors.ShouldContain(static e => e.Code == "CustomerName");
        validationError.Errors.ShouldContain(static e => e.Code == "CustomerEmail");

        _pipeline.Invoices.DidNotReceiveWithAnyArgs().Add(default!);
        await _pipeline.UnitOfWork.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_domain_operation_does_not_commit_unit_of_work()
    {
        _pipeline.Invoices.GetByIdAsync(Arg.Any<InvoiceId>(), Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var handler = _pipeline.Resolve<ICommandHandler<Features.Invoices.PayInvoice.PayInvoiceCommand>>();

        var result = await handler.Handle(
            new Features.Invoices.PayInvoice.PayInvoiceCommand(InvoiceId.New()),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        await _pipeline.UnitOfWork.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrency_conflict_from_the_unit_of_work_becomes_a_Conflict_result()
    {
        _pipeline.UnitOfWork.CommitAsync(Arg.Any<CancellationToken>()).Returns(Result.Failure(UnitOfWorkErrors.ConcurrencyConflict));
        var handler = _pipeline.Resolve<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();

        Result<InvoiceId> result = await handler.Handle(
            new CreateInvoiceCommand("ACME", "a@b.co", "USD"), TestContext.Current.CancellationToken);

        // The handler succeeded; the commit did not — the caller sees the commit's failure, not the id.
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(UnitOfWorkErrors.ConcurrencyConflict);
        result.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task Query_pipeline_never_calls_the_unit_of_work()
    {
        Invoice invoice = InvoiceTestData.Draft();
        _pipeline.Invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        var handler = _pipeline.Resolve<IQueryHandler<GetInvoiceQuery, InvoiceResponse>>();

        Result<InvoiceResponse> result = await handler.Handle(new GetInvoiceQuery(invoice.Id), TestContext.Current.CancellationToken);

        // A SUCCESSFUL query: the one outcome on which a command would have committed.
        result.IsSuccess.ShouldBeTrue();
        await _pipeline.UnitOfWork.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Every_use_case_resolves_through_the_exact_decorator_sequence()
    {
        // Discover every command/query in the assembly by reflection — a hand-maintained list silently
        // stops covering new slices (it once missed all three Auth commands).
        List<(Type Message, Type HandlerService)> useCases = [.. typeof(DependencyInjection).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(static t => t.GetInterfaces()
                .Select(i => PipelineHarness.HandlerServiceFor(t, i))
                .OfType<Type>()
                .Select(handler => (Message: t, HandlerService: handler)))];

        // Auth (5) + Invoices (7) today — a floor so a refactor that hides slices from the scan fails loudly.
        useCases.Count.ShouldBeGreaterThanOrEqualTo(12);

        foreach ((Type message, Type handlerService) in useCases)
        {
            IReadOnlyList<Type> stages = PipelineHarness.StagesOf(_pipeline.Provider.GetRequiredService(handlerService), handlerService);
            IReadOnlyList<Type> expected = handlerService.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)
                ? PipelineHarness.QueryStages
                : PipelineHarness.CommandStages;

            // Walks the whole chain, not just the outermost wrapper: a decorator registered in the
            // wrong place (or twice, or not at all) changes the sequence.
            stages.Take(stages.Count - 1).ShouldBe(expected, $"{message.Name} resolved through the wrong pipeline");
            Type handler = stages[^1];
            handler.Assembly.ShouldBe(typeof(DependencyInjection).Assembly, $"{message.Name}: the innermost stage must be its handler");
            handlerService.IsAssignableFrom(handler).ShouldBeTrue();
        }

        _pipeline.Resolve<IDomainEventDispatcher>().ShouldNotBeNull();
    }

    [Fact]
    public async Task An_application_without_queries_boots_and_wraps_its_commands()
    {
        // The remove-feature path: delete the Invoices slice and only Auth commands remain. Scrutor's
        // Decorate throws DecorationException when it finds nothing to wrap; the pipeline must not.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_pipeline.UnitOfWork);
        services.AddScoped<ICommandHandler<PingCommand>, PingCommandHandler>();

        Should.NotThrow(() => services.AddUseCasePipeline());
        await using ServiceProvider provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<ICommandHandler<PingCommand>>();
        PipelineHarness.StagesOf(handler, typeof(ICommandHandler<PingCommand>))
            .ShouldBe([.. PipelineHarness.CommandStages, typeof(PingCommandHandler)]);
        (await handler.Handle(new PingCommand(), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await _pipeline.UnitOfWork.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    private sealed record PingCommand : ICommand;

    private sealed class PingCommandHandler : ICommandHandler<PingCommand>
    {
        public Task<Result> Handle(PingCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }
}
