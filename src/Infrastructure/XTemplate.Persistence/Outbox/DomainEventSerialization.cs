using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence.Outbox;

/// <summary>Serializes domain events for the outbox and resolves their CLR types on the way back.</summary>
internal static class DomainEventSerialization
{
    // Strongly-typed IDs round-trip as their inner GUID via this factory (see SharedKernel).
    public static readonly JsonSerializerOptions Options = CreateOptions();

    // FullName → Type map of every IDomainEvent in the domain assembly (built once).
    private static readonly FrozenDictionary<string, Type> EventTypes = BuildEventTypeMap();

    public static string TypeNameOf(IDomainEvent domainEvent) => domainEvent.GetType().FullName!;

    public static string Serialize(IDomainEvent domainEvent)
        => JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options);

    public static IDomainEvent? Deserialize(string typeName, string content)
        => EventTypes.TryGetValue(typeName, out Type? type)
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
        Assembly domainAssembly = typeof(XTemplate.Domain.Invoices.Invoice).Assembly;

        return domainAssembly.GetTypes()
            .Where(static t => t is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToFrozenDictionary(static t => t.FullName!, static t => t);
    }
}
