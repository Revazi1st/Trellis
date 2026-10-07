using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Options;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Decorators;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests;

/// <summary>Exercises the real DI container: AddApplication() wiring, Scrutor scanning, and decorator behavior end-to-end.</summary>
public sealed class DecoratorPipelineTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ServiceProvider _provider;

    public DecoratorPipelineTests()
    {
        // NSubstitute cannot invent a Result: an unconfigured CommitAsync would hand the decorator null.
        _unitOfWork.CommitAsync(Arg.Any<CancellationToken>()).Returns(Result.Success());

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_invoices);
        services.AddSingleton(_unitOfWork);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AuthTokenOptions());      // the non-interface handler dependencies:
        services.AddSingleton(new RegistrationOptions());   // options types, projected as plain singletons
        services.AddApplication();

        // Fake every remaining outward port any handler needs — discovered, not hand-listed, so new
        // slices' ports are covered automatically. Ports are the non-generic interfaces in constructor
        // signatures; generic interfaces here are the pipeline's own contracts (handlers, validators).
        foreach (Type port in typeof(DependencyInjection).Assembly.GetTypes()
                     .Where(static t => t is { IsClass: true, IsAbstract: false })
                     .SelectMany(static t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                     .SelectMany(static c => c.GetParameters())
                     .Select(static p => p.ParameterType)
                     .Where(static t => t.IsInterface && !t.IsGenericType)
                     .Distinct()
                     .Where(port => services.All(d => d.ServiceType != port)))
        {
            services.AddSingleton(port, Substitute.For([port], []));
        }

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
        await _unitOfWork.Received(1).CommitAsync(Arg.Any<CancellationToken>());
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
        await _unitOfWork.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
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
        await _unitOfWork.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Every_use_case_resolves_through_the_full_decorator_pipeline()
    {
        // Discover every command/query in the assembly by reflection — a hand-maintained list silently
        // stops covering new slices (it once missed all three Auth commands).
        List<(Type Message, Type HandlerService)> useCases = [.. typeof(DependencyInjection).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(static t => t.GetInterfaces()
                .Select(i => HandlerServiceFor(t, i))
                .OfType<Type>()
                .Select(handler => (Message: t, HandlerService: handler)))];

        // Auth (3) + Invoices (7) today — a floor so a refactor that hides slices from the scan fails loudly.
        useCases.Count.ShouldBeGreaterThanOrEqualTo(10);

        foreach ((Type message, Type handlerService) in useCases)
        {
            object handler = _provider.GetRequiredService(handlerService);

            // The outermost wrapper must be the validation decorator — this pins the documented
            // Validation → Logging → UnitOfWork → handler order for every use case, current and future.
            handler.GetType().DeclaringType.ShouldBe(typeof(ValidationDecorators),
                $"{message.Name} did not resolve through the decorator pipeline");
        }

        _provider.GetRequiredService<IDomainEventDispatcher>().ShouldNotBeNull();
    }

    private static Type? HandlerServiceFor(Type message, Type contract)
    {
        if (contract == typeof(ICommand))
        {
            return typeof(ICommandHandler<>).MakeGenericType(message);
        }

        if (!contract.IsGenericType)
        {
            return null;
        }

        Type definition = contract.GetGenericTypeDefinition();
        if (definition == typeof(ICommand<>))
        {
            return typeof(ICommandHandler<,>).MakeGenericType(message, contract.GenericTypeArguments[0]);
        }

        return definition == typeof(IQuery<>)
            ? typeof(IQueryHandler<,>).MakeGenericType(message, contract.GenericTypeArguments[0])
            : null;
    }
}
