using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;

namespace PouryaKit.Testing;

/// <summary>
/// Sends an Operation through a Gateway from a test that does not care who runs it: each call runs as a new
/// Operation of <see cref="TestActor"/>. A test that needs a particular Actor, tenant or correlation passes its own
/// <see cref="OperationContext"/> to the Gateway's own methods.
/// </summary>
public static class GatewayTestExtensions
{
    /// <summary>The named system Actor a test's Operations run as when the test passes no context.</summary>
    public static Actor TestActor { get; } = Actor.System("PouryaKit.Testing");

    extension<TModule>(IGatewayProcessor<TModule> gateway)
        where TModule : IModuleDefinition
    {
        public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default) =>
            gateway.SendAsync(command, new OperationContext(TestActor), cancellationToken);

        public Task<Result<TResponse>> SendAsync<TResponse>(
            Command<TResponse> command,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull => gateway.SendAsync(command, new OperationContext(TestActor), cancellationToken);

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            Query<TResponse> query,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull => gateway.QueryAsync(query, new OperationContext(TestActor), cancellationToken);
    }
}
