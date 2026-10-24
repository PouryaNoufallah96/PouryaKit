using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Grpc;

// A gRPC service declared with AddPouryaKitGrpcService that no MapPouryaKitGrpcServices mapped: every call to it would
// answer Unimplemented. A startup check, as Web's UnmappedEndpointsCheck: the application maps its routes after the
// Host's container is built.
internal sealed class UnmappedGrpcServicesCheck : IStartupCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var mapped = services.GetRequiredService<MappedGrpcServices>();
        if (services.GetServices<GrpcService>().Where(service => !mapped.Contains(service.Type)).ToList() is [_, ..] unmapped)
        {
            yield return $"The gRPC services {string.Join(", ", unmapped.Select(service => service.Type.FullName))} are declared but never mapped, so every call to them answers Unimplemented. Call app.MapPouryaKitGrpcServices() once the Host is built.";
        }
    }
}

// The gRPC services MapPouryaKitGrpcServices mapped in this Host, by type. It maps them while the Host is composed,
// before it starts, so nothing writes this while the check reads it.
internal sealed class MappedGrpcServices
{
    private readonly HashSet<Type> _types = [];

    public bool Add(Type service) => _types.Add(service);

    public bool Contains(Type service) => _types.Contains(service);
}
