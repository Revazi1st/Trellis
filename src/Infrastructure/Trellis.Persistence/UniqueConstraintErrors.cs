using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Auth;
using Trellis.Persistence.Idempotency;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Results;

namespace Trellis.Persistence;

/// <summary>
/// The unique constraints whose violation is an expected outcome, each with the <see cref="Error"/> it
/// stands for (ADR-0018, amendment of 2026-10-11). A constraint is listed by the name its configuration
/// pins, never matched by pattern: a unique violation nobody registered here is a fault the model did not
/// foresee and still throws — a 500 with an Error log, not a guessed 409.
/// <para>
/// Registering one is a deliberate act: pin the name in the entity's configuration, add the row below,
/// and race it in a test (<c>RegistrationRaceTests</c>, <c>IdempotencyStoreTests</c>).
/// </para>
/// </summary>
internal static class UniqueConstraintErrors
{
    private static readonly FrozenDictionary<string, Error> ByConstraintName = new Dictionary<string, Error>(StringComparer.Ordinal)
    {
        // Two first requests with one Idempotency-Key raced to the commit; the winner's reservation stands.
        [IdempotencyKeyConfiguration.PrimaryKeyName] = IdempotencyErrors.Duplicate,

        // Two registrations of one e-mail both passed Identity's lookups; its unique user name (the e-mail) decides.
        [ApplicationUserConfiguration.UserNameIndexName] = AuthErrors.EmailAlreadyInUse,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The error a registered unique constraint's violation stands for. False for every other failure,
    /// which the caller lets propagate.
    /// </summary>
    public static bool TryMap(DbUpdateException exception, [NotNullWhen(true)] out Error? error)
    {
        if (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: { } constraintName,
            })
        {
            return ByConstraintName.TryGetValue(constraintName, out error);
        }

        error = null;
        return false;
    }
}
