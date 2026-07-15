namespace XTemplate.SharedKernel.Specifications;

/// <summary>A page of results plus the total count, so callers can render paging controls.</summary>
public sealed record PagedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public bool HasPreviousPage => Page > 1;

    public PagedList<TOut> Map<TOut>(Func<T, TOut> selector)
        => new([.. Items.Select(selector)], Page, PageSize, TotalCount);
}
