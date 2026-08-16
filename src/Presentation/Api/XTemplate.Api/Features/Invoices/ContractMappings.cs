using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices;

namespace XTemplate.Api.Features.Invoices;

internal static class ContractMappings
{
    public static XTemplate.Api.Contracts.Common.PagedResponse<TOut> ToPagedResponse<TIn, TOut>(
        this XTemplate.SharedKernel.Specifications.PagedList<TIn> page, System.Func<TIn, TOut> map)
        => new(
            [.. System.Linq.Enumerable.Select(page.Items, map)],
            page.Page, page.PageSize, page.TotalCount, page.TotalPages, page.HasNextPage, page.HasPreviousPage);

    public static InvoiceDto ToDto(this InvoiceResponse response)
        => new(
            response.Id,
            response.CustomerName,
            response.CustomerEmail,
            response.Currency,
            response.Status,
            response.DueDate,
            response.PaidAtUtc,
            response.Total,
            response.Version,
            [.. response.Lines.Select(static line => new InvoiceLineDto(line.Id, line.Description, line.Amount))]);
}
