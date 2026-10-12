#if (IncludeUI)
using System.Net;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The typed invoice client's lists and conditional writes: the invoice list sends a status only when one
/// is chosen (without it the API lists every invoice), and a write to an existing invoice carries the
/// version the page loaded as a strong <c>If-Match</c>, so the API answers 412 rather than act on a state
/// the user never saw; without a version the write is unconditional, as before.
/// </summary>
public sealed class InvoiceApiClientTests
{
    [Theory]
    [InlineData(null, 1, "?page=1&pageSize=20")]
    [InlineData(null, 3, "?page=3&pageSize=20")]
    [InlineData(InvoiceStatus.Draft, 1, "?status=Draft&page=1&pageSize=20")]
    [InlineData(InvoiceStatus.Issued, 1, "?status=Issued&page=1&pageSize=20")]
    [InlineData(InvoiceStatus.Paid, 2, "?status=Paid&page=2&pageSize=20")]
    [InlineData(InvoiceStatus.Voided, 1, "?status=Voided&page=1&pageSize=20")]
    public async Task The_invoice_list_sends_the_status_only_when_one_is_chosen(InvoiceStatus? status, int page, string query)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => Responses.Json(EmptyPage));

        (await ui.InvoiceApi.ListAsync(status, page, cancellationToken: ct)).IsSuccess.ShouldBeTrue();

        RecordedRequest request = ui.Api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("GET");
        request.Path.ShouldBe("/api/v1/invoices");
        request.Query.ShouldBe(query);
    }

    [Fact]
    public async Task The_overdue_list_keeps_its_own_route()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => Responses.Json(EmptyPage));

        (await ui.InvoiceApi.GetOverdueAsync(2, cancellationToken: ct)).IsSuccess.ShouldBeTrue();

        RecordedRequest request = ui.Api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/api/v1/invoices/overdue");
        request.Query.ShouldBe("?page=2&pageSize=20");
    }

    private static PagedResponse<InvoiceDto> EmptyPage => new([], Page: 1, PageSize: 20, TotalCount: 0, TotalPages: 0, HasNextPage: false, HasPreviousPage: false);

    [Fact]
    public async Task Writes_to_an_invoice_send_the_loaded_version_as_If_Match()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.NoContent));
        var id = Guid.CreateVersion7();

        (await ui.InvoiceApi.AddLineAsync(id, new AddInvoiceLineRequest("Line", 1m), 41, ct)).IsSuccess.ShouldBeTrue();
        (await ui.InvoiceApi.IssueAsync(id, new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(1)), 42, ct)).IsSuccess.ShouldBeTrue();
        (await ui.InvoiceApi.PayAsync(id, 43, ct)).IsSuccess.ShouldBeTrue();
        (await ui.InvoiceApi.VoidAsync(id, 44, ct)).IsSuccess.ShouldBeTrue();

        ui.Api.Requests.Select(static request => request.IfMatch).ShouldBe(["\"41\"", "\"42\"", "\"43\"", "\"44\""]);
    }

    [Fact]
    public async Task A_write_without_a_version_sends_no_If_Match()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.NoContent));

        (await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), cancellationToken: ct)).IsSuccess.ShouldBeTrue();

        ui.Api.Requests.ShouldHaveSingleItem().IfMatch.ShouldBeNull();
    }
}
#endif
