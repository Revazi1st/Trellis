using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Trellis.Api.Common.Endpoints;

public static class EndpointExtensions
{
    /// <summary>
    /// Maps every IEndpoint in the assembly onto the given builder (typically a versioned route group)
    /// and names each route after its endpoint class. The name is what <c>CreatedAtRoute</c> and link
    /// generation resolve, and what the OpenAPI document emits as the operation's <c>operationId</c>
    /// (ADR-0025) — so an endpoint cannot ship without one, and the id is always the class name.
    /// </summary>
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder builder, Assembly assembly)
    {
        IEnumerable<Type> endpointTypes = assembly
            .GetTypes()
            .Where(static type => type is { IsAbstract: false, IsInterface: false }
                && type.IsAssignableTo(typeof(IEndpoint)));

        foreach (Type endpointType in endpointTypes)
        {
            MethodInfo map = endpointType.GetMethod(
                nameof(IEndpoint.Map),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)])
                ?? throw new InvalidOperationException($"{endpointType.Name} implements IEndpoint but exposes no public static Map.");

            var endpoint = (RouteHandlerBuilder)map.Invoke(null, [builder])!;
            endpoint.WithName(endpointType.Name);
        }

        return builder;
    }
}
