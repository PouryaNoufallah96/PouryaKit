using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Unversioned;

/// <summary>Marks the assembly whose one endpoint declares no API version.</summary>
public static class UnversionedEndpoints;

internal sealed class ReadActorEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/actor", (OperationContext context) => context.Actor.Id);
}
