namespace XTemplate.Api.Common.Caching;

/// <summary>Cache-key conventions shared by read and write endpoints so invalidation stays in sync with reads.</summary>
public static class CacheKeys
{
    public static string Invoice(Guid id) => $"invoice:{id}";
}
