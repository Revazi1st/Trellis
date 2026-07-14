using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.SharedKernel.Decorators;
using XTemplate.SharedKernel.Domain;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application;

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
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        // Decoration order: first Decorate call is the innermost wrapper.
        // Effective pipeline: Validation → Logging → UnitOfWork → handler.
        services.Decorate(typeof(ICommandHandler<>), typeof(UnitOfWorkDecorators.CommandHandler<>));
        services.Decorate(typeof(ICommandHandler<,>), typeof(UnitOfWorkDecorators.CommandHandler<,>));

        services.Decorate(typeof(ICommandHandler<>), typeof(LoggingDecorators.CommandHandler<>));
        services.Decorate(typeof(ICommandHandler<,>), typeof(LoggingDecorators.CommandHandler<,>));
        services.Decorate(typeof(IQueryHandler<,>), typeof(LoggingDecorators.QueryHandler<,>));

        services.Decorate(typeof(ICommandHandler<>), typeof(ValidationDecorators.CommandHandler<>));
        services.Decorate(typeof(ICommandHandler<,>), typeof(ValidationDecorators.CommandHandler<,>));
        services.Decorate(typeof(IQueryHandler<,>), typeof(ValidationDecorators.QueryHandler<,>));

        return services;
    }
}
