using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices;

namespace XTemplate.Api.Features.Invoices;

internal static class ContractMappings
{
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
            [.. response.Lines.Select(static line => new InvoiceLineDto(line.Id, line.Description, line.Amount))]);
}
