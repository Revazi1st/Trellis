using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Trellis.Domain.Common;

namespace Trellis.Persistence;

/// <summary>
/// Maps a single-value value object (<see cref="IScalarValueObject{TSelf, TValue}"/>) to one plain column
/// instead of a complex type. Value objects stay complex types by default (ADR-0006); this is for the one
/// whose column needs an index, a key or a <c>Contains</c> query, which EF Core 10 cannot give a
/// complex-type member. One line per value-object type in <c>AppDbContext.ConfigureConventions</c>:
/// <code>
/// configurationBuilder.Properties&lt;EmailAddress&gt;().HaveConversion&lt;ScalarValueObjectConverter&lt;EmailAddress, string&gt;&gt;();
/// </code>
/// which makes every member of that type a converted column (its <c>ComplexProperty</c> mappings go), so
/// a value-object type is one or the other throughout the model. The price: a query compares the whole
/// value (<c>==</c>, <c>Contains</c>, ordering) but no longer reaches inside it, so
/// <c>Email.Value.StartsWith(…)</c> stops translating to SQL. Reading a row calls
/// <see cref="IScalarValueObject{TSelf, TValue}.Restore"/>, which skips <c>Create</c>'s checks, as the
/// complex-type materializer does.
/// </summary>
public sealed class ScalarValueObjectConverter<TValueObject, TValue>() : ValueConverter<TValueObject, TValue>(
    valueObject => valueObject.Value,
    value => Restore(value))
    where TValueObject : class, IScalarValueObject<TValueObject, TValue>
    where TValue : notnull
{
    // Expression trees cannot invoke static abstract interface members directly (CS8927); this indirection keeps the converter generic.
    private static TValueObject Restore(TValue value) => TValueObject.Restore(value);
}
