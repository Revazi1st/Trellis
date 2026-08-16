using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence;

/// <summary>Maps any IStronglyTypedId&lt;TId&gt; to its inner GUID column. Registered per ID type in AppDbContext.ConfigureConventions.</summary>
public sealed class StronglyTypedIdValueConverter<TId>() : ValueConverter<TId, Guid>(
    id => id.Value,
    value => FromGuid(value))
    where TId : struct, IStronglyTypedId<TId>
{
    // Expression trees cannot invoke static abstract interface members directly (CS8927); this indirection keeps the converter generic.
    private static TId FromGuid(Guid value) => TId.From(value);
}
