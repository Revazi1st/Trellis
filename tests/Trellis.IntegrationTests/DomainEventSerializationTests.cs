using System.Reflection;
using Shouldly;
using Trellis.Persistence.Outbox;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Fitness test for the outbox contract: every domain event must carry a unique stable wire name
/// and must JSON round-trip. A new event with an unsupported member type would otherwise fail
/// silently at dispatch time in production.
/// </summary>
public sealed class DomainEventSerializationTests
{
    private static List<Type> EventTypes => [.. typeof(Trellis.Domain.Invoices.Invoice).Assembly.GetTypes()
        .Where(static t => t is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(t))];

    [Fact]
    public void Every_domain_event_has_a_unique_stable_wire_name()
    {
        List<Type> events = EventTypes;
        events.ShouldNotBeEmpty();

        var names = new List<string>();
        foreach (Type eventType in events)
        {
            DomainEventNameAttribute? attribute = eventType.GetCustomAttribute<DomainEventNameAttribute>();
            attribute.ShouldNotBeNull($"{eventType.Name} needs [DomainEventName(\"...\")] — outbox rows must survive type renames");
            names.Add(attribute.Name);
        }

        names.ShouldBeUnique(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_domain_event_round_trips_through_the_outbox_serializer()
    {
        foreach (Type eventType in EventTypes)
        {
            IDomainEvent original = CreateSample(eventType);

            string serialized = DomainEventSerialization.Serialize(original);
            IDomainEvent? deserialized = DomainEventSerialization.Deserialize(
                DomainEventSerialization.TypeNameOf(original), serialized);

            deserialized.ShouldNotBeNull($"{eventType.Name} did not deserialize");
            deserialized.ShouldBe(original, $"{eventType.Name} did not round-trip losslessly (events are records — value equality)");
        }
    }

    /// <summary>
    /// Builds an instance with synthesized values for every constructor parameter. Only the types
    /// domain events are allowed to carry are supported — an exotic member type fails here, which
    /// is the point: it would break outbox dispatch in production.
    /// </summary>
    private static IDomainEvent CreateSample(Type eventType)
    {
        ConstructorInfo constructor = eventType.GetConstructors().OrderByDescending(static c => c.GetParameters().Length).First();

        object?[] arguments = [.. constructor.GetParameters().Select(parameter => SampleValue(parameter.ParameterType, eventType))];
        return (IDomainEvent)constructor.Invoke(arguments);
    }

    private static object? SampleValue(Type type, Type eventType)
    {
        if (type == typeof(string))
        {
            return "sample";
        }

        if (type == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(double))
        {
            return Convert.ChangeType(42, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type == typeof(bool))
        {
            return true;
        }

        // Strongly-typed IDs round-trip via the converter factory; construct through New().
        if (type.IsValueType && Array.Exists(
                type.GetInterfaces(),
                static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>)))
        {
            return type.GetMethod("New", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }

        throw new InvalidOperationException(
            $"Domain event {eventType.Name} carries a '{type.Name}' member. Events crossing the outbox must use "
            + "primitives or strongly-typed IDs so they JSON round-trip (see docs/features/invoices.md).");
    }
}
