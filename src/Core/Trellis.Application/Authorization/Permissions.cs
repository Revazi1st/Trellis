namespace Trellis.Application.Authorization;

/// <summary>
/// The permission catalogue — the single source of truth for authorization. Endpoints require these
/// via RequirePermission(...); roles are granted these as claims and seeded by the MigrationService.
/// </summary>
public static class Permissions
{
    public static class Invoices
    {
        public const string Read = "invoices:read";
        public const string Write = "invoices:write";
    }

    /// <summary>Every permission constant declared above — used by seeding to grant the Administrator role everything.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Invoices.Read,
        Invoices.Write,
    ];
}

/// <summary>The seeded roles and the permissions each is granted.</summary>
public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Member = "Member";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Permissions { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Administrator] = Authorization.Permissions.All,
            [Member] = [Authorization.Permissions.Invoices.Read],
        };
}
