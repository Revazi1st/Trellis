namespace XTemplate.SharedKernel.Domain;

/// <summary>
/// Contract for hand-rolled strongly-typed IDs (ADR-0005). Implement as:
/// <code>
/// public readonly record struct InvoiceId(Guid Value) : IStronglyTypedId&lt;InvoiceId&gt;
/// {
///     public static InvoiceId New() => new(Guid.CreateVersion7());
///     public static InvoiceId From(Guid value) => new(value);
///     public override string ToString() => Value.ToString();
/// }
/// </code>
/// JSON conversion is generic (see StronglyTypedIdJsonConverterFactory); the EF value converter lives in Persistence.
/// </summary>
public interface IStronglyTypedId<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}
