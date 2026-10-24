namespace Trellis.ServiceDefaults;

/// <summary>
/// Aspire resource names, spelled once. The AppHost declares the database and every service under one of these
/// names, and the code that finds a resource by name (a connection string, a service-discovery address, the E2E
/// test's waits) uses the same constant, so renaming a resource is one edit that compiles everywhere instead of a
/// host that fails at boot. Names nothing looks up stay where they are declared (the publish-mode parameters,
/// the compose environment). The AppHost reaches this class through an <c>IsAspireProjectResource="false"</c>
/// reference: it compiles against ServiceDefaults rather than running it. JSON, launch settings and YAML cannot
/// use a constant and keep the literal names.
/// </summary>
public static class ServiceNames
{
    public const string Postgres = "postgres";
    public const string Database = "trellis-db";
    public const string Migrations = "migrations";
    public const string Api = "api";
    public const string Web = "web";
#if (UseRedis)
    public const string Cache = "cache";
#endif
}
