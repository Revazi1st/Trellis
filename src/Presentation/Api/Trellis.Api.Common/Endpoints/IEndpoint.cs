using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Trellis.Api.Common.Endpoints;

/// <summary>
/// One class per use case; Map wires the route to a static handler. Discovered by MapEndpoints at startup.
/// <para>
/// Map returns the builder so that <c>MapEndpoints</c> can finish every endpoint the same way: it names
/// the route after the class (<c>.WithName(nameof(IssueInvoice))</c>), which is also the operation's
/// <c>operationId</c> in the OpenAPI document (ADR-0025). Refer to another endpoint's route with
/// <c>nameof</c> (<c>TypedResults.CreatedAtRoute(..., nameof(GetInvoice), ...)</c>); never call
/// <c>.WithName</c> yourself.
/// </para>
/// <code>
/// internal sealed class IssueInvoice : IEndpoint
/// {
///     public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app.MapPost("invoices/{id}/issue", Handle)...;
///     private static async Task&lt;Results&lt;NoContent, ProblemHttpResult&gt;&gt; Handle(...) => ...;
/// }
/// </code>
/// </summary>
public interface IEndpoint
{
    static abstract RouteHandlerBuilder Map(IEndpointRouteBuilder app);
}
