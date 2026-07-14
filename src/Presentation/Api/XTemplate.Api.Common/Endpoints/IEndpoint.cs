using Microsoft.AspNetCore.Routing;

namespace XTemplate.Api.Common.Endpoints;

/// <summary>
/// One class per use case; Map wires the route to a static handler. Discovered by MapEndpoints at startup.
/// <code>
/// internal sealed class IssueInvoice : IEndpoint
/// {
///     public static void Map(IEndpointRouteBuilder app) => app.MapPost("invoices/{id}/issue", Handle)...;
///     private static async Task&lt;Results&lt;NoContent, ProblemHttpResult&gt;&gt; Handle(...) => ...;
/// }
/// </code>
/// </summary>
public interface IEndpoint
{
    static abstract void Map(IEndpointRouteBuilder app);
}
