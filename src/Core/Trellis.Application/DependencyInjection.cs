using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trellis.Application.Observability;
using Trellis.SharedKernel.Decorators;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application;

public static class DependencyInjection
{
    /// <summary>Registers every handler in this assembly and wraps commands/queries in the decorator pipeline (ADR-0003).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblies(typeof(DependencyInjection).Assembly)
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(IDomainEventHandler<>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime());

        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddSingleton<InvoiceMetrics>();
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        return services.AddUseCasePipeline();
    }

    /// <summary>
    /// Wraps every registered command and query handler in the use-case pipeline:
    /// <b>Tracing → Logging → Validation → UnitOfWork → handler</b> (queries skip UnitOfWork — they never
    /// commit). Tracing and Logging are outermost so a rejected request and a failing commit both still
    /// produce their span and outcome line. Tracing wraps Logging, so the outcome line is written while the
    /// use-case span is still current and carries its trace and span ids.
    /// <para>
    /// <c>TryDecorate</c>, not <c>Decorate</c>: Scrutor's <c>Decorate</c> throws when it finds nothing to
    /// wrap, so a project with no query (the remove-feature path) or no command would not boot.
    /// </para>
    /// </summary>
    internal static IServiceCollection AddUseCasePipeline(this IServiceCollection services)
    {
        // The outcome line's defaults (no error descriptions); a host that binds the Telemetry section
        // registers its own instance, which wins whether it is added before this call or after it.
        services.TryAddSingleton(new UseCaseLoggingOptions());

        // The FIRST call is the INNERMOST wrapper: read this block bottom-up to follow a request in.
        services.TryDecorate(typeof(ICommandHandler<>), typeof(UnitOfWorkDecorators.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(UnitOfWorkDecorators.CommandHandler<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(ValidationDecorators.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationDecorators.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationDecorators.QueryHandler<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(LoggingDecorators.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingDecorators.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingDecorators.QueryHandler<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(TracingDecorators.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TracingDecorators.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(TracingDecorators.QueryHandler<,>));

        return services;
    }
}
