#if (IncludeUI)
using System.Net;
using Shouldly;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The typed invoice client's conditional writes: a write to an existing invoice carries the version the
/// page loaded as a strong <c>If-Match</c>, so the API answers 412 rather than act on a state the user
/// never saw; without a version the write is unconditional, as before.
/// </summary>
public sealed class InvoiceApiClientTests
{
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
