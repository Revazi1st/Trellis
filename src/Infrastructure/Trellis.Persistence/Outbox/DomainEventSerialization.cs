using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// Serializes domain events for the outbox and resolves their types on the way back — keyed by the
/// event's stable <see cref="DomainEventNameAttribute"/> name, never the CLR type: renaming or
/// moving a class must not orphan in-flight rows during a rolling deploy.
/// </summary>
internal static class DomainEventSerialization
{
    // Strongly-typed IDs round-trip as their inner GUID via this factory (see SharedKernel).
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly FrozenDictionary<string, Type> TypesByName = BuildEventTypeMap();

    private static readonly FrozenDictionary<Type, string> NamesByType =
        TypesByName.ToFrozenDictionary(static pair => pair.Value, static pair => pair.Key);

    public static string TypeNameOf(IDomainEvent domainEvent)
        => NamesByType.TryGetValue(domainEvent.GetType(), out string? name)
            ? name
            : throw MissingName(domainEvent.GetType());

    public static string Serialize(IDomainEvent domainEvent)
        => JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options);

    public static IDomainEvent? Deserialize(string typeName, string content)
        => TypesByName.TryGetValue(typeName, out Type? type)
            ? (IDomainEvent?)JsonSerializer.Deserialize(content, type, Options)
            : null;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StronglyTypedIdJsonConverterFactory());
        return options;
    }

    private static FrozenDictionary<string, Type> BuildEventTypeMap()
    {
        // The Invoice aggregate anchors the domain assembly.
        Assembly domainAssembly = typeof(Trellis.Domain.Invoices.Invoice).Assembly;

        return domainAssembly.GetTypes()
            .Where(static t => t is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToFrozenDictionary(
                static t => t.GetCustomAttribute<DomainEventNameAttribute>()?.Name ?? throw MissingName(t),
                static t => t);
    }

    private static InvalidOperationException MissingName(Type eventType) => new(
        $"Domain event '{eventType.FullName}' has no [DomainEventName(\"...\")]. Every event needs a stable, "
        + "versioned wire name so outbox rows survive refactoring (see the serialization fitness test).");
}
