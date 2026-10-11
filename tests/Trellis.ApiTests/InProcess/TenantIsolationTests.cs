#if (UseMultitenancy)
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Options;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// Tenant isolation over the REAL HTTP pipeline (ADR-0022): the tenant comes from the signed claim
/// the API minted from the user's membership and from nothing the caller controls, and every read,
/// write, listing and cache hit is scoped to it. Compiled only with the multi-tenancy option — the
/// template's all-options CI leg runs it.
/// </summary>
public sealed class TenantIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>The header the resolver used to honour. It must stay dead, whoever re-adds a reader.</summary>
    private const string TenantHeader = "X-Tenant-Id";

    [Fact]
    public async Task Invoice_created_in_tenant_A_is_404_for_tenant_B_on_read_and_write()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient tenantA = await factory.CreateAdminClientAsync(ApiFactory.TenantA, ct);
        HttpClient tenantB = await factory.CreateAdminClientAsync(ApiFactory.TenantB, ct);
        Guid id = await tenantA.CreateInvoiceAsync();

        HttpResponseMessage read = await tenantB.GetAsync($"/api/v1/invoices/{id}", ct);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        read.ShouldBeProblemJson();

        // Writes are isolated too: the handler loads through the filtered repository, so the other
        // tenant's row does not exist as far as the command is concerned.
        HttpResponseMessage write = await tenantB.PostAsJsonAsync(
            $"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Smuggled line", 1m), ct);
        write.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage own = await tenantA.GetAsync($"/api/v1/invoices/{id}", ct);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.Content.ReadFromJsonAsync<InvoiceDto>(ct))!.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Overdue_listing_contains_only_the_callers_tenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient tenantA = await factory.CreateAdminClientAsync(ApiFactory.TenantA, ct);
        HttpClient tenantB = await factory.CreateAdminClientAsync(ApiFactory.TenantB, ct);
        Guid id = await tenantA.CreateInvoiceAsync();

        await tenantA.AddInvoiceLineAsync(id);
        // A due date is never past when issued: issue it due a minute from now on the host's clock, then
        // move that clock past it instead of sleeping until the invoice is genuinely overdue.
        await tenantA.IssueInvoiceAsync(id, dueDate: factory.Time.GetUtcNow().AddMinutes(1));
        factory.Time.Advance(TimeSpan.FromMinutes(2));

        try
        {
            PagedResponse<InvoiceDto> seenByB = await ListOverdueAsync(tenantB, ct);
            seenByB.TotalCount.ShouldBe(0);
            seenByB.Items.ShouldNotContain(invoice => invoice.Id == id);

            PagedResponse<InvoiceDto> seenByA = await ListOverdueAsync(tenantA, ct);
            seenByA.Items.ShouldContain(invoice => invoice.Id == id);
        }
        finally
        {
            factory.Time.Reset();   // the fixture is shared by the class
        }
    }

    [Fact]
    public async Task X_Tenant_Id_header_never_overrides_the_claim()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient tenantA = await factory.CreateAdminClientAsync(ApiFactory.TenantA, ct);
        HttpClient tenantB = await factory.CreateAdminClientAsync(ApiFactory.TenantB, ct);

        // Naming another tenant on a write does not move the row: it is stamped from the claim.
        using var createAsB = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(new CreateInvoiceRequest("Header Corp", "h@h.co", "USD")),
        };
        createAsB.Headers.Add(TenantHeader, ApiFactory.TenantB);
        createAsB.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        HttpResponseMessage created = await tenantA.SendAsync(createAsB, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid id = (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct))!.Id;

        // Naming the owning tenant on a read does not grant it: the claim still says tenant B.
        using var readAsA = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invoices/{id}");
        readAsA.Headers.Add(TenantHeader, ApiFactory.TenantA);
        (await tenantB.SendAsync(readAsA, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await tenantA.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // And a header is not a credential.
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invoices/{id}");
        anonymous.Headers.Add(TenantHeader, ApiFactory.TenantA);
        (await factory.CreateClient().SendAsync(anonymous, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cache_warmed_by_tenant_A_does_not_serve_tenant_B()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient tenantA = await factory.CreateAdminClientAsync(ApiFactory.TenantA, ct);
        HttpClient tenantB = await factory.CreateAdminClientAsync(ApiFactory.TenantB, ct);
        Guid id = await tenantA.CreateInvoiceAsync();

        // A cache hit bypasses the EF filter, which is why CacheKeys carries the tenant: warm the
        // entry as A, then prove B still misses, and that B's negative lookup did not evict A's entry.
        (await tenantA.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await tenantB.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantA.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Access_token_carries_the_users_tenant_claim()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        TokenResponse seededAdmin = await factory.LoginAsAdminAsync(ct);
        TenantClaimOf(seededAdmin.AccessToken).ShouldBe(ApiFactory.TenantA);

        HttpClient tenantB = await factory.CreateAdminClientAsync(ApiFactory.TenantB, ct);
        TenantClaimOf(tenantB.DefaultRequestHeaders.Authorization!.Parameter!).ShouldBe(ApiFactory.TenantB);
    }

    [Fact]
    public async Task Token_without_a_tenant_claim_is_rejected_with_401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Control: the same signature, issuer, audience and permission WITH the claim is accepted,
        // so the only thing the rejection below can be about is the missing tenant.
        (await CallWithMintedTokenAsync(tenant: ApiFactory.TenantA, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage response = await CallWithMintedTokenAsync(tenant: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.ShouldBeProblemJson();
        response.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_token");
    }

    private static string? TenantClaimOf(string accessToken)
        => new JsonWebToken(accessToken).TryGetClaim(ITenantContext.ClaimType, out Claim? claim) ? claim.Value : null;

    private static async Task<PagedResponse<InvoiceDto>> ListOverdueAsync(HttpClient client, CancellationToken ct)
    {
        HttpResponseMessage response = await client.GetAsync("/api/v1/invoices/overdue", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResponse<InvoiceDto>>(ct))!;
    }

    /// <summary>A token signed with the host's own key (as a pre-toggle token or an external IdP would be), with or without the tenant.</summary>
    private async Task<HttpResponseMessage> CallWithMintedTokenAsync(string? tenant, CancellationToken ct)
    {
        AuthTokenOptions options = factory.Services.GetRequiredService<IOptions<AuthTokenOptions>>().Value;
        string subject = Guid.NewGuid().ToString();
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, subject),
            new(ClaimTypes.NameIdentifier, subject),
            new(AuthClaims.Permission, Permissions.Invoices.Read),
        ];
        if (tenant is not null)
        {
            claims.Add(new Claim(ITenantContext.ClaimType, tenant));
        }

        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = options.Issuer,
            Audience = options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256),
        });

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/api/v1/invoices/overdue", ct);
    }
}
#endif
