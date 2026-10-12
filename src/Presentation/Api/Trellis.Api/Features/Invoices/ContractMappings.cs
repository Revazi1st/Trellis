using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Features.Invoices;

namespace Trellis.Api.Features.Invoices;

internal static class ContractMappings
{
    public static Trellis.Api.Contracts.Common.PagedResponse<TOut> ToPagedResponse<TIn, TOut>(
        this Trellis.SharedKernel.Specifications.PagedList<TIn> page, System.Func<TIn, TOut> map)
        => new(
            [.. System.Linq.Enumerable.Select(page.Items, map)],
            page.Page, page.PageSize, page.TotalCount, page.TotalPages, page.HasNextPage, page.HasPreviousPage);

    public static InvoiceDto ToDto(this InvoiceResponse response)
        => new(
            response.Id,
            response.CustomerName,
            response.CustomerEmail,
            response.Currency,
            // By name: the Contracts enum mirrors the domain's member for member (an architecture test holds that).
            Enum.Parse<InvoiceStatus>(response.Status),
            response.CreatedAtUtc,
            response.DueDate,
            response.PaidAtUtc,
            response.Total,
            response.Version,
            [.. response.Lines.Select(static line => new InvoiceLineDto(line.Id, line.Description, line.Amount))]);

    /// <summary>
    /// The wire status as the domain's, by name like <see cref="ToDto"/>. A value the enum does not define (an
    /// ordinal the binder accepted) stays undefined, and the query's validator refuses it naming the field.
    /// </summary>
    public static Trellis.Domain.Invoices.InvoiceStatus ToDomain(this InvoiceStatus status)
        => Enum.Parse<Trellis.Domain.Invoices.InvoiceStatus>(status.ToString());
}
