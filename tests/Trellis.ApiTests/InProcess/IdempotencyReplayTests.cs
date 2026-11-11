using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Api.Common.Http;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The Idempotency-Key contract (ADR-0018) through the real HTTP pipeline: a retry of a completed request
/// gets the stored response back, a reused key is refused, a concurrent duplicate loses, a failed command
/// leaves nothing behind, and a response over the replay cap completes without becoming replayable.
/// </summary>
public sealed class IdempotencyReplayTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Retrying_a_committed_request_replays_the_original_response()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        string key = Guid.NewGuid().ToString();
        var request = new CreateInvoiceRequest("Replay Ltd", "retry@replay.io", "USD");

        HttpResponseMessage first = await PostCreateAsync(admin, key, request, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
        byte[] firstBody = await first.Content.ReadAsByteArrayAsync(ct);

        HttpResponseMessage retry = await PostCreateAsync(admin, key, request, ct);

        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        retry.Headers.Location.ShouldNotBeNull().ShouldBe(first.Headers.Location);
        // Both sides present and equal: a replay that dropped the header must not pass because the first lacked it too.
        string firstContentType = first.ShouldHaveMediaType("application/json").ToString();
        retry.ShouldHaveMediaType("application/json").ToString().ShouldBe(firstContentType);
        (await retry.Content.ReadAsByteArrayAsync(ct)).ShouldBe(firstBody);

        // The replayed id is the first one, and it is a real invoice: one command ran, not two.
        Guid id = JsonSerializer.Deserialize<CreateInvoiceResponse>(firstBody, JsonSerializerOptions.Web).ShouldNotBeNull().Id;
        (await admin.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Retrying_a_committed_request_without_a_response_body_replays_its_204()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid invoiceId = await admin.CreateInvoiceAsync();
        string key = Guid.NewGuid().ToString();

        (await AddLineAsync(admin, invoiceId, key, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage retry = await AddLineAsync(admin, invoiceId, key, ct);

        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        retry.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        (await admin.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{invoiceId}", ct)).ShouldNotBeNull()
            .Lines.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_422_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        string key = Guid.NewGuid().ToString();
        (await PostCreateAsync(admin, key, new CreateInvoiceRequest("Replay Ltd", "retry@replay.io", "USD"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage response = await PostCreateAsync(admin, key, new CreateInvoiceRequest("Someone Else", "retry@replay.io", "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.UnprocessableEntity, "Idempotency.FingerprintMismatch", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Unprocessable request");
        problem.Detail.ShouldNotBeNull().ShouldContain("different request");
        problem.Errors.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_first_requests_yield_one_201_and_one_409_and_the_retry_replays_the_winner()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Both requests must pass the lookup before either commits, or the second is merely a replay of
        // the first: a derived host holds every commit until two have arrived, then releases both, and
        // the IdempotencyKeys primary key decides — the loser's whole unit of work rolls back.
        var barrier = new CommitBarrier(participants: 2);
        var racing = factory.WithTestServices(services => services.AddScoped<IUnitOfWork>(provider =>
            new BarrierUnitOfWork(provider.GetRequiredService<AppDbContext>(), barrier)));
        HttpClient client = await factory.CreateAdminClientOnAsync(racing, ct);
        string key = Guid.NewGuid().ToString();
        var request = new CreateInvoiceRequest("Racing Ltd", "race@replay.io", "USD");

        HttpResponseMessage[] responses = await Task.WhenAll(
            PostCreateAsync(client, key, request, ct),
            PostCreateAsync(client, key, request, ct));

        HttpResponseMessage created = responses.Single(static r => r.StatusCode == HttpStatusCode.Created);
        created.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
        HttpResponseMessage conflict = responses.Single(static r => r.StatusCode == HttpStatusCode.Conflict);
        ProblemResponse problem = await ShouldBeProblemAsync(conflict, HttpStatusCode.Conflict, "Idempotency.Duplicate", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Conflict");
        problem.Errors.ShouldBeNull();

        HttpResponseMessage retry = await PostCreateAsync(client, key, request, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        (await retry.Content.ReadAsByteArrayAsync(ct)).ShouldBe(await created.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>
    /// The primary key decides a race only when both requests reach their commit. A feature with a unique
    /// business key (an e-mail, a code) lets the loser fail on the winner's data first: its uniqueness check
    /// finds the row the winner just committed, or the business index fires before the primary key. The
    /// loser must not answer the feature's 409 for a create that succeeded, so the filter asks the store
    /// again and answers from the winner's reservation. The derived host's create handler stands in for that
    /// check: the first request waits until the second is past the lookup, then runs the real pipeline and
    /// commits; the second waits for that commit and answers <c>Test.Taken</c>.
    /// </summary>
    [Fact]
    public async Task A_concurrent_first_that_fails_on_the_winners_data_is_answered_from_the_store_not_with_its_own_409()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var race = new UniquenessRace();
        var racing = factory.WithTestServices(services =>
            Wrap<ICommandHandler<CreateInvoiceCommand, InvoiceId>>(services, inner => new RacingCreateHandler(inner, race)));
        HttpClient client = await factory.CreateAdminClientOnAsync(racing, ct);
        string key = Guid.NewGuid().ToString();
        var request = new CreateInvoiceRequest("Unique Key Ltd", "unique@replay.io", "USD");

        HttpResponseMessage[] responses = await Task.WhenAll(
            PostCreateAsync(client, key, request, ct),
            PostCreateAsync(client, key, request, ct));

        HttpResponseMessage created = responses.Single(static r =>
            r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains(IdempotencyFilter.ReplayedHeaderName));
        byte[] createdBody = await created.Content.ReadAsByteArrayAsync(ct);
        HttpResponseMessage loser = responses.Single(r => !ReferenceEquals(r, created));
        string loserBody = await loser.Content.ReadAsStringAsync(ct);
        loserBody.ShouldNotContain(RacingCreateHandler.Taken.Code);
        if (loser.StatusCode == HttpStatusCode.Created)
        {
            // The winner had stored its response by the time the loser asked again: a replay.
            loser.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
            (await loser.Content.ReadAsByteArrayAsync(ct)).ShouldBe(createdBody);
        }
        else
        {
            // Still in flight, or completed without a replayable body: a 409 the client knows how to read.
            loser.StatusCode.ShouldBe(HttpStatusCode.Conflict, loserBody);
            loser.ShouldBeProblemJson();
            JsonSerializer.Deserialize<ProblemResponse>(loserBody, JsonSerializerOptions.Web).ShouldNotBeNull(loserBody)
                .Code.ShouldBeOneOf(IdempotencyErrors.InFlight.Code, IdempotencyErrors.Duplicate.Code);
        }

        // One invoice: the key's retry replays the winner.
        HttpResponseMessage retry = await PostCreateAsync(client, key, request, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        (await retry.Content.ReadAsByteArrayAsync(ct)).ShouldBe(createdBody);
    }

    [Fact]
    public async Task A_retry_while_the_first_request_is_still_running_is_a_409_in_flight_until_it_completes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient healthy = await factory.CreateAdminClientAsync(ct);
        Guid invoiceId = await healthy.CreateInvoiceAsync();

        // The gate opens after the command committed and before the response goes out: the exact window
        // in which a second request finds the reservation but no response.
        var gate = new PostCommitGate();
        var gated = factory.WithTestServices(services => Wrap<HybridCache>(services, inner => new GatedHybridCache(inner, gate)));
        HttpClient client = await factory.CreateAdminClientOnAsync(gated, ct);
        string key = Guid.NewGuid().ToString();

        Task<HttpResponseMessage> first = AddLineAsync(client, invoiceId, key, ct);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        HttpResponseMessage inFlight = await AddLineAsync(client, invoiceId, key, ct);
        ProblemResponse problem = await ShouldBeProblemAsync(inFlight, HttpStatusCode.Conflict, "Idempotency.InFlight", $"/api/v1/invoices/{invoiceId}/lines", ct);
        problem.Title.ShouldBe("Conflict");
        inFlight.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));

        gate.Release.SetResult();
        (await first).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage replay = await AddLineAsync(client, invoiceId, key, ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        replay.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        (await healthy.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{invoiceId}", ct)).ShouldNotBeNull()
            .Lines.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_failed_command_leaves_no_reservation_so_the_key_can_be_reused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        string key = Guid.NewGuid().ToString();

        HttpResponseMessage failed = await PostCreateAsync(admin, key, new CreateInvoiceRequest("", "not-an-email", "USD"), ct);
        failed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Had the reservation survived, a different body under the same key would be a 422 now.
        HttpResponseMessage retry = await PostCreateAsync(admin, key, new CreateInvoiceRequest("Second Try Ltd", "second@replay.io", "USD"), ct);

        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
    }

    [Fact]
    public async Task A_response_over_the_replay_cap_completes_but_its_retry_is_a_409_duplicate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var host = factory.WithTestServices(static services => services.AddTransient<IStartupFilter, CappedEndpointStartupFilter>());
        HttpClient client = host.CreateClient();

        string oversizedKey = Guid.NewGuid().ToString();
        HttpResponseMessage first = await PostCappedAsync(client, oversizedKey, CappedEndpointStartupFilter.Cap + 1, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
        (await first.Content.ReadFromJsonAsync<CappedResponse>(ct)).ShouldNotBeNull().Payload.Length.ShouldBe(CappedEndpointStartupFilter.Cap + 1);

        HttpResponseMessage retry = await PostCappedAsync(client, oversizedKey, CappedEndpointStartupFilter.Cap + 1, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ProblemResponse problem = (await retry.Content.ReadFromJsonAsync<ProblemResponse>(JsonSerializerOptions.Web, ct)).ShouldNotBeNull();
        problem.Code.ShouldBe("Idempotency.Duplicate");
        problem.Detail.ShouldNotBeNull().ShouldContain("not available for replay");

        // The cap, not the endpoint, decides: a response that fits is replayed.
        string smallKey = Guid.NewGuid().ToString();
        HttpResponseMessage small = await PostCappedAsync(client, smallKey, 8, ct);
        small.StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage smallRetry = await PostCappedAsync(client, smallKey, 8, ct);
        smallRetry.StatusCode.ShouldBe(HttpStatusCode.OK);
        smallRetry.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        (await smallRetry.Content.ReadAsByteArrayAsync(ct)).ShouldBe(await small.Content.ReadAsByteArrayAsync(ct));
    }

    private static async Task<ProblemResponse> ShouldBeProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string instance, CancellationToken ct)
    {
        response.StatusCode.ShouldBe(status);
        response.ShouldBeProblemJson();
        string body = await response.Content.ReadAsStringAsync(ct);
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(body, JsonSerializerOptions.Web).ShouldNotBeNull(body);
        problem.Status.ShouldBe((int)status, body);
        problem.Code.ShouldBe(code, body);
        problem.Type.ShouldBe(ProblemTypes.For(code), body);
        problem.Instance.ShouldBe(instance, body);
        problem.CorrelationId.ShouldBe(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(), body);
        return problem;
    }

    private static Task<HttpResponseMessage> PostCreateAsync(HttpClient client, string key, CreateInvoiceRequest request, CancellationToken ct)
        => SendWithKeyAsync(client, "/api/v1/invoices", key, request, ct);

    private static Task<HttpResponseMessage> AddLineAsync(HttpClient client, Guid invoiceId, string key, CancellationToken ct)
        => SendWithKeyAsync(client, $"/api/v1/invoices/{invoiceId}/lines", key, new AddInvoiceLineRequest("Replayed line", 10m), ct);

    private static Task<HttpResponseMessage> PostCappedAsync(HttpClient client, string key, int size, CancellationToken ct)
        => SendWithKeyAsync(client, CappedEndpointStartupFilter.Route, key, new CappedRequest(size), ct);

    private static async Task<HttpResponseMessage> SendWithKeyAsync<TBody>(HttpClient client, string uri, string key, TBody body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        message.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return await client.SendAsync(message, ct);
    }

    /// <summary>Wraps the app's own registration of <typeparamref name="TService"/>, whatever it is, keeping its lifetime.</summary>
    private static void Wrap<TService>(IServiceCollection services, Func<TService, TService> wrap)
        where TService : class
    {
        ServiceDescriptor original = services.Last(static d => d.ServiceType == typeof(TService) && !d.IsKeyedService);
        services.Remove(original);
        services.Add(new ServiceDescriptor(
            typeof(TService),
            provider => wrap((TService)(original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!))),
            original.Lifetime));
    }

    /// <summary>Releases every arrival once <paramref name="participants"/> have arrived: the two racing commits start together.</summary>
    private sealed class CommitBarrier(int participants)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= participants)
            {
                _released.TrySetResult();
            }

            return _released.Task;
        }
    }

    /// <summary>The real unit of work (the request's own <see cref="AppDbContext"/>, which also tracks the reservation), held at the barrier before it commits.</summary>
    private sealed class BarrierUnitOfWork(AppDbContext inner, CommitBarrier barrier) : IUnitOfWork
    {
        public async Task<Result> CommitAsync(CancellationToken cancellationToken)
        {
            await barrier.ArriveAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return await inner.CommitAsync(cancellationToken);
        }
    }

    /// <summary>Which of two racing creates arrived first, and the two moments the other one waits for.</summary>
    private sealed class UniquenessRace
    {
        private int _arrived;

        public TaskCompletionSource SecondArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ArriveFirst() => Interlocked.Increment(ref _arrived) == 1;
    }

    /// <summary>
    /// Outermost around the app's create pipeline. The first arrival commits through it once the second has
    /// passed the filter's lookup; the second never reaches it and fails the way a feature's uniqueness check
    /// fails once the winner's row exists.
    /// </summary>
    private sealed class RacingCreateHandler(ICommandHandler<CreateInvoiceCommand, InvoiceId> inner, UniquenessRace race)
        : ICommandHandler<CreateInvoiceCommand, InvoiceId>
    {
        public static readonly Error Taken = Error.Conflict("Test.Taken", "The unique key is taken by the request that committed first.");

        public async Task<Result<InvoiceId>> Handle(CreateInvoiceCommand command, CancellationToken cancellationToken)
        {
            if (race.ArriveFirst())
            {
                await race.SecondArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                Result<InvoiceId> created = await inner.Handle(command, cancellationToken);
                race.FirstCommitted.TrySetResult();
                return created;
            }

            race.SecondArrived.TrySetResult();
            await race.FirstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return Taken;
        }
    }

    private sealed class PostCommitGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The app's cache, except that the first eviction (post-commit work) waits at the gate. Everything else is delegated untouched.</summary>
    private sealed class GatedHybridCache(HybridCache inner, PostCommitGate gate) : HybridCache
    {
        private int _evictions;

        public override async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _evictions) == 1)
            {
                gate.Reached.TrySetResult();
                await gate.Release.Task;
            }

            await inner.RemoveAsync(key, cancellationToken);
        }

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

    /// <summary>
    /// No shipped endpoint answers more than a few hundred bytes, so the cap is exercised on a test-only
    /// endpoint with a small one: the real filter, the real unit of work (the reservation must commit),
    /// mapped in a branch of its own ahead of the app's pipeline so it needs no token.
    /// </summary>
    private sealed class CappedEndpointStartupFilter : IStartupFilter
    {
        public const string Route = "/__test/idempotency/capped";
        public const int Cap = 64;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/__test/idempotency", static branch =>
            {
                branch.UseRouting();
                branch.UseEndpoints(static endpoints => endpoints
                    .MapPost("/capped", static async Task<Ok<CappedResponse>> (CappedRequest request, IUnitOfWork unitOfWork, CancellationToken ct) =>
                    {
                        (await unitOfWork.CommitAsync(ct)).ShouldBeSuccess();
                        return TypedResults.Ok(new CappedResponse(new string('x', request.Size)));
                    })
                    .RequireIdempotency(maxReplayBodyBytes: Cap));
            });

            next(app);
        };
    }

    private sealed record CappedRequest(int Size);

    private sealed record CappedResponse(string Payload);
}
