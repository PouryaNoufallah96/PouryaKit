using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;

namespace PouryaKit.Testing;

/// <summary>
/// Sends an Operation through a Gateway from a test that does not care who runs it: each call runs as a new
/// Operation of <see cref="TestActor"/>. A test that needs a particular Actor, tenant or correlation passes its own
/// <see cref="OperationContext"/> to the Gateway's own methods.
/// </summary>
/// <remarks>
/// These overloads take no context, and still satisfy the rule that an Operation never runs as a default Actor
/// (ADR 0007): a test is independent system work under an explicit, named identity, and they exist only in
/// <c>PouryaKit.Testing</c>, which application code never references.
/// </remarks>
public static class GatewayTestExtensions
{
    /// <summary>The named system Actor a test's Operations run as when the test passes no context.</summary>
    public static Actor TestActor { get; } = Actor.System("PouryaKit.Testing");

    extension<TModule>(IGatewayProcessor<TModule> gateway)
        where TModule : IModuleDefinition
    {
        /// <summary>
        /// Runs <paramref name="command"/> through the Gateway as a new Operation of <see cref="TestActor"/> and
        /// returns its Result.
        /// </summary>
        public Task<Result> SendAsync(
            Command command,
            CancellationToken cancellationToken = default
        ) => gateway.SendAsync(command, new OperationContext(TestActor), cancellationToken);

        /// <summary>
        /// Runs <paramref name="command"/> through the Gateway as a new Operation of <see cref="TestActor"/> and
        /// returns its Result, which holds the response on success.
        /// </summary>
        public Task<Result<TResponse>> SendAsync<TResponse>(
            Command<TResponse> command,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull =>
            gateway.SendAsync(command, new OperationContext(TestActor), cancellationToken);

        /// <summary>
        /// Runs <paramref name="query"/> through the Gateway as a new Operation of <see cref="TestActor"/> and returns
        /// its Result, which holds the answer on success.
        /// </summary>
        public Task<Result<TResponse>> QueryAsync<TResponse>(
            Query<TResponse> query,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull =>
            gateway.QueryAsync(query, new OperationContext(TestActor), cancellationToken);
    }
}
