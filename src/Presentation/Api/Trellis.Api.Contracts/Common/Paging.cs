namespace Trellis.Api.Contracts.Common;

/// <summary>
/// The bounds every paged list accepts: <c>?page=</c> (1..10000) and <c>?pageSize=</c> (1..100). Endpoints
/// declare them inline with <c>[Range]</c>, so the OpenAPI document states them; the Application validators
/// enforce them (a value outside answers 400 naming the field). The page cap keeps deep offsets bounded.
/// </summary>
public static class PagingLimits
{
    public const int MinPage = 1;
    public const int MaxPage = 10_000;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
}

/// <summary>A page of results returned to clients, with the metadata to render paging controls.</summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasNextPage,
    bool HasPreviousPage);
