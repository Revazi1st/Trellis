#if (IncludeUI)
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.UI.Common.Pages;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The invoice pages as a signed-in user sees them: the list calls the API the filter in the address names
/// (<c>?status=</c>, so Back and a bookmark keep it), marks that filter current, and names it in the empty
/// state and the pager; the detail and create pages open with a link back to the list. Rendered to HTML by
/// the framework's <see cref="HtmlRenderer"/> over the real <c>AddSharedUi</c> graph and a scripted API: no
/// browser, no circuit, so a click is out of reach here (the Playwright flow in Trellis.UiTests clicks).
/// </summary>
public sealed partial class InvoicePagesTests
{
    [Theory]
    [InlineData("invoices", "/api/v1/invoices", "?page=1&pageSize=20", "All")]
    [InlineData("", "/api/v1/invoices", "?page=1&pageSize=20", "All")]
    [InlineData("invoices?status=Draft", "/api/v1/invoices", "?status=Draft&page=1&pageSize=20", "Draft")]
    [InlineData("invoices?status=Issued", "/api/v1/invoices", "?status=Issued&page=1&pageSize=20", "Issued")]
    [InlineData("invoices?status=Paid", "/api/v1/invoices", "?status=Paid&page=1&pageSize=20", "Paid")]
    [InlineData("invoices?status=Voided", "/api/v1/invoices", "?status=Voided&page=1&pageSize=20", "Voided")]
    [InlineData("invoices?status=Overdue", "/api/v1/invoices/overdue", "?page=1&pageSize=20", "Overdue")]
    [InlineData("invoices?status=overdue", "/api/v1/invoices/overdue", "?page=1&pageSize=20", "Overdue")]
    [InlineData("invoices?status=bogus", "/api/v1/invoices", "?page=1&pageSize=20", "All")]
    public async Task The_list_asks_for_what_the_address_filters_and_marks_that_filter_current(
        string address, string path, string query, string current)
    {
        await using PageHost host = await PageHost.OpenAsync(address, static _ => Responses.Json(Page(totalCount: 0)));

        string html = await host.RenderAsync<Invoices>();

        RecordedRequest request = host.Api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe(path);
        request.Query.ShouldBe(query);
        CurrentFilters(html).ShouldBe([current]);
    }

    [Fact]
    public async Task The_filter_row_is_a_labelled_group_of_links_to_every_filter()
    {
        await using PageHost host = await PageHost.OpenAsync("invoices", static _ => Responses.Json(Page(totalCount: 0)));

        string html = await host.RenderAsync<Invoices>();

        html.ShouldContain("<h1>Invoices</h1>");
        html.ShouldContain("""role="group" aria-labelledby="invoice-filter-label">""");
        html.ShouldContain("""<span id="invoice-filter-label" class="filters-label">Show</span>""");
        FilterLinks(html).ShouldBe(
        [
            ("invoices", "All"),
            ("invoices?status=Draft", "Draft"),
            ("invoices?status=Issued", "Issued"),
            ("invoices?status=Paid", "Paid"),
            ("invoices?status=Voided", "Voided"),
            ("invoices?status=Overdue", "Overdue"),
        ]);
    }

    [Theory]
    [InlineData("invoices", "No invoices yet.")]
    [InlineData("invoices?status=Draft", "No draft invoices.")]
    [InlineData("invoices?status=Voided", "No voided invoices.")]
    [InlineData("invoices?status=Overdue", "No overdue invoices.")]
    public async Task The_empty_state_names_the_filter(string address, string empty)
    {
        await using PageHost host = await PageHost.OpenAsync(address, static _ => Responses.Json(Page(totalCount: 0)));

        string html = await host.RenderAsync<Invoices>();

        html.ShouldContain($"<p>{empty}</p>");
        html.ShouldNotContain("<table");
    }

    [Theory]
    [InlineData("invoices", 42, "Page 1 of 3 (42 invoices)")]
    [InlineData("invoices", 1, "Page 1 of 1 (1 invoice)")]
    [InlineData("invoices?status=Issued", 21, "Page 1 of 2 (21 issued invoices)")]
    [InlineData("invoices?status=Overdue", 42, "Page 1 of 3 (42 overdue invoices)")]
    public async Task The_pager_names_the_filter_and_counts_its_invoices(string address, int totalCount, string pager)
    {
        await using PageHost host = await PageHost.OpenAsync(address, _ => Responses.Json(Page(totalCount, Invoice("Ada Lovelace"))));

        string html = await host.RenderAsync<Invoices>();

        html.ShouldContain(pager);
    }

    [Fact]
    public async Task A_list_that_cannot_load_says_so_and_keeps_the_filter_row()
    {
        await using PageHost host = await PageHost.OpenAsync("invoices?status=Paid", static _ => Responses.Status(HttpStatusCode.ServiceUnavailable));

        string html = await host.RenderAsync<Invoices>();

        html.ShouldContain("""<p class="error" role="alert">""");
        html.ShouldNotContain("Loading");
        CurrentFilters(html).ShouldBe(["Paid"]);
    }

    [Fact]
    public async Task Every_row_shows_when_the_invoice_was_created()
    {
        await using PageHost host = await PageHost.OpenAsync("invoices", static _ => Responses.Json(Page(1, Invoice("Ada Lovelace"))));

        string html = await host.RenderAsync<Invoices>();

        html.ShouldContain("<th>Created</th>");
        html.ShouldContain("<td>Ada Lovelace</td>");
        html.ShouldContain("<td>2026-10-05</td>");
    }

    [Fact]
    public async Task The_invoice_page_opens_with_a_link_back_to_the_list()
    {
        InvoiceDto invoice = Invoice("Ada Lovelace");
        await using PageHost host = await PageHost.OpenAsync($"invoices/{invoice.Id}", _ => Responses.Json(invoice));

        string html = await host.RenderAsync<InvoiceDetail>(new Dictionary<string, object?>(StringComparer.Ordinal) { [nameof(InvoiceDetail.Id)] = invoice.Id });

        html.ShouldContain("<h1>Ada Lovelace</h1>");
        BackLinkPrecedes(html, "<h1>").ShouldBeTrue(html);
    }

    [Fact]
    public async Task The_invoice_page_keeps_the_way_back_when_the_invoice_cannot_be_loaded()
    {
        var missing = Guid.CreateVersion7();
        await using PageHost host = await PageHost.OpenAsync(
            $"invoices/{missing}",
            static _ => Responses.Problem(HttpStatusCode.NotFound, """{"status":404,"code":"Invoice.NotFound","title":"Not found","detail":"No invoice has that id."}"""));

        string html = await host.RenderAsync<InvoiceDetail>(new Dictionary<string, object?>(StringComparer.Ordinal) { [nameof(InvoiceDetail.Id)] = missing });

        html.ShouldContain("role=\"alert\"");
        BackLinkPrecedes(html, "role=\"alert\"").ShouldBeTrue(html);
    }

    [Fact]
    public async Task The_new_invoice_page_opens_with_a_link_back_to_the_list()
    {
        await using PageHost host = await PageHost.OpenAsync(
            "invoices/new", static _ => Responses.Status(HttpStatusCode.NoContent), permissions: ["invoices:read", "invoices:write"]);

        string html = await host.RenderAsync<CreateInvoice>();

        html.ShouldContain("<h1>New invoice</h1>");
        BackLinkPrecedes(html, "<h1>").ShouldBeTrue(html);
        host.Navigation.Navigations.ShouldBeEmpty("a writer stays on the page");
    }

    private static PagedResponse<InvoiceDto> Page(int totalCount, params InvoiceDto[] items)
    {
        int totalPages = (int)Math.Ceiling(totalCount / (double)PagingLimits.DefaultPageSize);
        return new(items, Page: 1, PageSize: PagingLimits.DefaultPageSize, totalCount, totalPages, HasNextPage: totalPages > 1, HasPreviousPage: false);
    }

    private static InvoiceDto Invoice(string customer) => new(
        Guid.CreateVersion7(),
        customer,
        "ada@example.com",
        "EUR",
        InvoiceStatus.Issued,
        CreatedAtUtc: new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero),
        DueDate: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        PaidAtUtc: null,
        Total: 1200m,
        Version: 7,
        Lines: [new InvoiceLineDto(Guid.CreateVersion7(), "Analytical engine consulting", 1200m)]);

    /// <summary>The labels of the filter links marked current.</summary>
    private static string[] CurrentFilters(string html)
        => [.. FilterLink().Matches(html).Where(static m => m.Groups["current"].Success).Select(static m => m.Groups["label"].Value)];

    private static (string Href, string Label)[] FilterLinks(string html)
        => [.. FilterLink().Matches(html).Select(static m => (m.Groups["href"].Value, m.Groups["label"].Value))];

    /// <summary>The page's "Back to invoices" link exists, points at the list, and comes before <paramref name="marker"/>.</summary>
    private static bool BackLinkPrecedes(string html, string marker)
    {
        Match back = BackLink().Match(html);
        int at = html.IndexOf(marker, StringComparison.Ordinal);
        return back.Success && at >= 0 && back.Index < at;
    }

    [GeneratedRegex("""<a href="(?<href>[^"]*)" class="filter"(?<current> aria-current="page")?>(?<label>[^<]+)</a>""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilterLink();

    [GeneratedRegex("""<a href="invoices" class="back"[^>]*>Back to invoices</a>""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BackLink();
}
#endif
