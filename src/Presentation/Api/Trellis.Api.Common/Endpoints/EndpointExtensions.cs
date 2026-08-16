using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace Trellis.Api.Common.Endpoints;

public static class EndpointExtensions
{
    /// <summary>Maps every IEndpoint in the assembly onto the given builder (typically a versioned route group).</summary>
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

            map.Invoke(null, [builder]);
        }

        return builder;
    }
}
