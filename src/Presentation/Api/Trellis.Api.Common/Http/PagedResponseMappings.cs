using Trellis.Api.Contracts.Common;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Api.Common.Http;

/// <summary>
/// The one mapping from a query's <see cref="PagedList{T}"/> to the wire's <see cref="PagedResponse{T}"/>: a list
/// endpoint answers <c>result.Value.ToPagedResponse(item => item.ToDto())</c> with its feature's item mapping,
/// so no slice writes the envelope's four counts and two flags out by position, where a swapped pair still
/// compiles. <c>ConventionTests</c> finds a per-feature copy.
/// </summary>
public static class PagedResponseMappings
{
    public static PagedResponse<TOut> ToPagedResponse<TIn, TOut>(this PagedList<TIn> page, Func<TIn, TOut> map)
        => new(
            [.. page.Items.Select(map)],
            Page: page.Page,
            PageSize: page.PageSize,
            TotalCount: page.TotalCount,
            TotalPages: page.TotalPages,
            HasNextPage: page.HasNextPage,
            HasPreviousPage: page.HasPreviousPage);
}
