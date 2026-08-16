namespace Trellis.Api.Contracts.Common;

/// <summary>Standard paging query parameters (bound from the query string).</summary>
public sealed record PageRequest(int Page = 1, int PageSize = 20);

/// <summary>A page of results returned to clients, with the metadata to render paging controls.</summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasNextPage,
    bool HasPreviousPage);
