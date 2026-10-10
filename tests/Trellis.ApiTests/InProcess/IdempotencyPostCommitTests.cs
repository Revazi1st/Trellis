using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// Nothing an endpoint does after its command committed may throw for an aborted request: the read-cache
/// invalidation takes <see cref="CancellationToken.None"/>. A throw there would end the request with a 500
/// after the reservation committed, and the client's retry would get a 409 instead of the stored 204
/// (ADR-0018). Proven on a derived host whose cache cancels whenever it is handed a cancellable token,
/// exactly what a client disconnect after the commit does.
/// </summary>
public sealed class IdempotencyPostCommitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_request_aborted_after_its_commit_keeps_the_key_and_the_retry_replays_the_204()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient healthy = await factory.CreateAdminClientAsync(ct);
        Guid invoiceId = await CreateInvoiceAsync(healthy, ct);

        var cancelling = factory.WithTestServices(WrapCacheInCancellingCache);
        HttpClient aborting = await factory.CreateAdminClientOnAsync(cancelling, ct);
        string key = Guid.NewGuid().ToString();

        // The command commits; the post-commit invalidation must not be cancellable by the request.
        (await AddLineAsync(aborting, invoiceId, key, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The client never saw that 204 and retries with the same key: the stored 204 again, not a second line.
        HttpResponseMessage retry = await AddLineAsync(aborting, invoiceId, key, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        retry.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");

        InvoiceDto invoice = (await healthy.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{invoiceId}", ct)).ShouldNotBeNull();
        invoice.Lines.ShouldHaveSingleItem().Description.ShouldBe("Post-commit");
    }

    /// <summary>Wraps the app's own <see cref="HybridCache"/> registration, whatever it is, in <see cref="CancellingHybridCache"/>.</summary>
    private static void WrapCacheInCancellingCache(IServiceCollection services)
    {
        ServiceDescriptor original = services.Last(static d => d.ServiceType == typeof(HybridCache));
        services.Remove(original);
        services.AddSingleton<HybridCache>(provider => new CancellingHybridCache((HybridCache)(original.ImplementationInstance
            ?? original.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!))));
    }

    private static async Task<Guid> CreateInvoiceAsync(HttpClient client, CancellationToken ct)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Post Commit Ltd", "p@c.io", "USD"), ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct)).ShouldNotBeNull().Id;
    }

    private static async Task<HttpResponseMessage> AddLineAsync(HttpClient client, Guid invoiceId, string idempotencyKey, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{invoiceId}/lines")
        {
            Content = JsonContent.Create(new AddInvoiceLineRequest("Post-commit", 10m)),
        };
        message.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        return await client.SendAsync(message, ct);
    }

    /// <summary>
    /// The app's cache, except that removing an entry with a cancellable token throws as if the request
    /// had just been aborted. Everything else is delegated untouched.
    /// </summary>
    private sealed class CancellingHybridCache(HybridCache inner) : HybridCache
    {
        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
            => cancellationToken.CanBeCanceled
                ? ValueTask.FromException(new OperationCanceledException("The client went away after the commit.", cancellationToken))
                : inner.RemoveAsync(key, cancellationToken);

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => inner.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken);

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => inner.SetAsync(key, value, options, tags, cancellationToken);

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
            => inner.RemoveByTagAsync(tag, cancellationToken);
    }
}
