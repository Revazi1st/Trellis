using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Trellis.Application.Options;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Decorators;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.UnitTests;

/// <summary>
/// The real <c>AddApplication()</c> container — Scrutor scan, validators, the decorator pipeline — with
/// every outward port faked and every log line captured. Pipeline tests resolve use cases from here.
/// </summary>
internal sealed class PipelineHarness : IDisposable
{
    /// <param name="configure">
    /// Runs before <c>AddApplication()</c>, so a test-only handler registered here is wrapped by exactly
    /// the pipeline a real slice gets.
    /// </param>
    public PipelineHarness(Action<IServiceCollection>? configure = null)
    {
        // NSubstitute cannot invent a Result: an unconfigured CommitAsync would hand the decorator null.
        UnitOfWork.CommitAsync(Arg.Any<CancellationToken>()).Returns(Result.Success());

        var services = new ServiceCollection();
        services.AddSingleton(Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        services.AddSingleton(Invoices);
        services.AddSingleton(UnitOfWork);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AuthTokenOptions());      // the non-interface handler dependencies:
        services.AddSingleton(new RegistrationOptions());   // options types, projected as plain singletons
        configure?.Invoke(services);
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

        Provider = services.BuildServiceProvider();
    }

    /// <summary>The decorator classes, outermost first, every command passes through before its handler.</summary>
    public static IReadOnlyList<Type> CommandStages { get; } =
        [typeof(TracingDecorators), typeof(LoggingDecorators), typeof(ValidationDecorators), typeof(UnitOfWorkDecorators)];

    /// <summary>The query pipeline: the command pipeline without UnitOfWork — queries never commit.</summary>
    public static IReadOnlyList<Type> QueryStages { get; } =
        [typeof(TracingDecorators), typeof(LoggingDecorators), typeof(ValidationDecorators)];

    public IInvoiceRepository Invoices { get; } = Substitute.For<IInvoiceRepository>();

    public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();

    public LogCapture Logs { get; } = new();

    public ServiceProvider Provider { get; }

    public T Resolve<T>()
        where T : notnull
        => Provider.GetRequiredService<T>();

    /// <summary>
    /// Walks a resolved handler's decorator chain by reflection, outermost first: each stage is the
    /// static decorator class it belongs to (<see cref="TracingDecorators"/>, …), the last one the
    /// concrete handler. Each decorator's captured <c>inner</c> field is the next link.
    /// </summary>
    public static IReadOnlyList<Type> StagesOf(object handler, Type handlerService)
    {
        List<Type> stages = [];
        for (object? current = handler; current is not null; current = InnerOf(current, handlerService))
        {
            Type type = current.GetType();
            stages.Add(type.DeclaringType is { } decorators && decorators.Namespace == typeof(LoggingDecorators).Namespace
                ? decorators
                : type);
        }

        return stages;
    }

    /// <summary>The handler-service interface a message is resolved through, or null for a non-message type.</summary>
    public static Type? HandlerServiceFor(Type message, Type contract)
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

    public void Dispose() => Provider.Dispose();

    private static object? InnerOf(object decorator, Type handlerService)
        => decorator.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType == handlerService)
            .Select(field => field.GetValue(decorator))
            .SingleOrDefault();
}
