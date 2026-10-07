using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Trellis.Persistence.Identity;

/// <summary>
/// Spends one password verification on a sign-in for an e-mail that matches no account, so the
/// response takes as long as a wrong password for a real one and its timing does not reveal which
/// e-mails are registered.
/// <para>
/// The hash is made once per process by the same <see cref="IPasswordHasher{TUser}"/> that verifies
/// real passwords. The hash records its own algorithm and iteration count, so verifying it costs
/// exactly what the configured hasher costs.
/// </para>
/// </summary>
internal static class DummyPasswordHash
{
    private static readonly ApplicationUser Nobody = new();
    private static string? Hash;

    public static void Verify(IPasswordHasher<ApplicationUser> hasher, string password)
    {
        string hash = LazyInitializer.EnsureInitialized(
            ref Hash, () => hasher.HashPassword(Nobody, RandomNumberGenerator.GetHexString(32)));

        // The outcome is irrelevant (a random secret never matches); only the work is wanted.
        _ = hasher.VerifyHashedPassword(Nobody, hash, password);
    }
}
