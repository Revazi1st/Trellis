using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// The Invoice use cases as one-line arrange steps over HTTP, paired with IntegrationTests'
/// <c>InvoiceDriver</c>: each sends a valid request (overridable per argument) as the client's user,
/// fails naming the status and the problem's <c>code</c> if the API refused it, and returns the invoice's
/// id. Arrange only: a test about an endpoint's answer sends its own request and asserts on the response
/// (<c>IdempotentPostExtensions</c> for a create with a fresh key). A value the test depends on is passed
/// at the call site, never left to a default.
/// </summary>
internal static class InvoiceHttpDriver
{
    public const string Customer = "ACME Corp";
    public const string Email = "billing@acme.com";
    public const string CurrencyCode = "USD";
    public const string LineDescription = "Consulting";
    public const decimal LineAmount = 100m;

    /// <summary>Creates a draft (<c>POST /invoices</c> requires an <c>Idempotency-Key</c>, so it sends a fresh one).</summary>
    public static async Task<Guid> CreateInvoiceAsync(
        this HttpClient client, string customerName = Customer, string customerEmail = Email, string currency = CurrencyCode)
    {
        using HttpResponseMessage created = await client.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices", new CreateInvoiceRequest(customerName, customerEmail, currency), Ct);
        await created.ShouldAnswerAsync(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(Ct)).ShouldNotBeNull().Id;
    }

    public static async Task<Guid> AddInvoiceLineAsync(
        this HttpClient client, Guid invoiceId, string description = LineDescription, decimal amount = LineAmount)
    {
        using HttpResponseMessage added = await client.PostAsJsonAsync(
            $"/api/v1/invoices/{invoiceId}/lines", new AddInvoiceLineRequest(description, amount), Ct);
        await added.ShouldAnswerAsync(HttpStatusCode.NoContent);
        return invoiceId;
    }

    /// <summary>The due date has no default: the host's clock (<c>ApiFactory.Time</c>) decides whether it is in the past.</summary>
    public static async Task<Guid> IssueInvoiceAsync(this HttpClient client, Guid invoiceId, DateTimeOffset dueDate)
    {
        using HttpResponseMessage issued = await client.PostAsJsonAsync(
            $"/api/v1/invoices/{invoiceId}/issue", new IssueInvoiceRequest(dueDate), Ct);
        await issued.ShouldAnswerAsync(HttpStatusCode.NoContent);
        return invoiceId;
    }

    public static async Task<Guid> PayInvoiceAsync(this HttpClient client, Guid invoiceId)
    {
        using HttpResponseMessage paid = await client.PostAsync($"/api/v1/invoices/{invoiceId}/pay", content: null, Ct);
        await paid.ShouldAnswerAsync(HttpStatusCode.NoContent);
        return invoiceId;
    }

    public static async Task<Guid> VoidInvoiceAsync(this HttpClient client, Guid invoiceId)
    {
        using HttpResponseMessage voided = await client.PostAsync($"/api/v1/invoices/{invoiceId}/void", content: null, Ct);
        await voided.ShouldAnswerAsync(HttpStatusCode.NoContent);
        return invoiceId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fails unless the API answered <paramref name="expected"/>, naming what it answered instead and why.</summary>
    private static async Task ShouldAnswerAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(Ct);
        string? code = null;
        try
        {
            code = JsonSerializer.Deserialize<ProblemResponse>(body, JsonSerializerOptions.Web)?.Code;
        }
        catch (JsonException)
        {
            // Not a problem document: the body below says what it is.
        }

        throw new ShouldAssertException(
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}{Environment.NewLine}" +
            $"    should answer {(int)expected} but answered {(int)response.StatusCode} {code ?? "(no problem code)"}{Environment.NewLine}" +
            $"    {body}");
    }
}
