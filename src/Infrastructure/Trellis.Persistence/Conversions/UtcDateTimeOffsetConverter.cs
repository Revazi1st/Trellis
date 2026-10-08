using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Trellis.Persistence;

/// <summary>
/// Normalises every <see cref="DateTimeOffset"/> to UTC on its way to PostgreSQL. A <c>timestamptz</c>
/// column stores an instant, and Npgsql refuses to write a value whose offset is not zero rather than
/// pick one — so a client's <c>2026-09-01T10:00:00+02:00</c> would fail the save (a 500) instead of being
/// stored as the instant it denotes. Values come back as UTC (offset zero). Registered for every
/// <see cref="DateTimeOffset"/> property, nullable ones included, in <c>AppDbContext.ConfigureConventions</c>;
/// the store type is unchanged, so the model (and the migration snapshot) is too.
/// </summary>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value);
