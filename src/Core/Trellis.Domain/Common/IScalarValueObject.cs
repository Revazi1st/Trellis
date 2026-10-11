namespace Trellis.Domain.Common;

/// <summary>
/// A value object that is exactly one scalar: <see cref="EmailAddress"/> is its string,
/// <see cref="Currency"/> its code. Implementing it changes nothing on its own; value objects map as EF
/// complex types (ADR-0006). It lets Persistence map one as a plain column through
/// <c>ScalarValueObjectConverter</c>, one line per type, when that column needs an index, a key or a
/// <c>Contains</c> query, none of which EF Core 10 offers on a complex-type member (ADR-0006, amendment
/// of 2026-10-11).
/// </summary>
/// <typeparam name="TSelf">The value object itself.</typeparam>
/// <typeparam name="TValue">The scalar it is stored as.</typeparam>
public interface IScalarValueObject<TSelf, TValue>
    where TSelf : IScalarValueObject<TSelf, TValue>
    where TValue : notnull
{
    /// <summary>The one value the object holds, as it is stored.</summary>
    TValue Value { get; }

    /// <summary>
    /// Rebuilds the value object from a value this application stored, without the checks of its
    /// <c>Create</c>: the value passed them when it was written, and a rule tightened since must not make
    /// old rows unreadable (a complex type is materialized the same way). Implement it explicitly
    /// (<c>static EmailAddress IScalarValueObject&lt;EmailAddress, string&gt;.Restore(string value)</c>) so
    /// it is reachable through the interface only, never as <c>EmailAddress.Restore</c> on input.
    /// </summary>
    static abstract TSelf Restore(TValue value);
}
