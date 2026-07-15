using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Domain;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;
using Xunit;

namespace XTemplate.Application.UnitTests;

/// <summary>Exercises the real DI container: AddApplication() wiring, Scrutor scanning, and decorator behavior end-to-end.</summary>
public sealed class DecoratorPipelineTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ServiceProvider _provider;

    public DecoratorPipelineTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_invoices);
        services.AddSingleton(_unitOfWork);
        services.AddSingleton(TimeProvider.System);
        services.AddApplication();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Valid_command_reaches_handler_and_commits_unit_of_work()
    {
        var handler = _provider.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();

        var result = await handler.Handle(
            new CreateInvoiceCommand("ACME", "a@b.co", "USD"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _invoices.ReceivedWithAnyArgs(1).Add(default!);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_command_short_circuits_before_handler_and_unit_of_work()
    {
        var handler = _provider.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();

        var result = await handler.Handle(
            new CreateInvoiceCommand(CustomerName: "", CustomerEmail: "", Currency: "USD"),
            TestContext.Current.CancellationToken);

        var validationError = result.Error.ShouldBeOfType<ValidationError>();
        validationError.Errors.Count.ShouldBe(2);
        validationError.Errors.ShouldContain(static e => e.Code == "CustomerName");
        validationError.Errors.ShouldContain(static e => e.Code == "CustomerEmail");

        _invoices.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_domain_operation_does_not_commit_unit_of_work()
    {
        _invoices.GetByIdAsync(Arg.Any<InvoiceId>(), Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var handler = _provider.GetRequiredService<ICommandHandler<Features.Invoices.PayInvoice.PayInvoiceCommand>>();

        var result = await handler.Handle(
            new Features.Invoices.PayInvoice.PayInvoiceCommand(InvoiceId.New()),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Every_use_case_resolves_from_the_container()
    {
        _provider.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>().ShouldNotBeNull();
        _provider.GetRequiredService<ICommandHandler<Features.Invoices.AddInvoiceLine.AddInvoiceLineCommand>>().ShouldNotBeNull();
        _provider.GetRequiredService<ICommandHandler<Features.Invoices.IssueInvoice.IssueInvoiceCommand>>().ShouldNotBeNull();
        _provider.GetRequiredService<ICommandHandler<Features.Invoices.PayInvoice.PayInvoiceCommand>>().ShouldNotBeNull();
        _provider.GetRequiredService<ICommandHandler<Features.Invoices.VoidInvoice.VoidInvoiceCommand>>().ShouldNotBeNull();
        _provider.GetRequiredService<IQueryHandler<Features.Invoices.GetInvoice.GetInvoiceQuery, Features.Invoices.InvoiceResponse>>().ShouldNotBeNull();
        _provider.GetRequiredService<IQueryHandler<Features.Invoices.ListOverdueInvoices.ListOverdueInvoicesQuery, XTemplate.SharedKernel.Specifications.PagedList<Features.Invoices.InvoiceResponse>>>().ShouldNotBeNull();
        _provider.GetRequiredService<IDomainEventDispatcher>().ShouldNotBeNull();
    }
}
