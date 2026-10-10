using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

/// <summary>
/// Begins an Operation on the service provider of its scope, as part of the Cqrs Capability, for the code that opens an
/// Operation's scope.
/// </summary>
public static class OperationContextServiceProviderExtensions
{
    extension(IServiceProvider scope)
    {
        /// <summary>
        /// Begins the Operation of the scope this provider belongs to, once, before anything in the scope runs: sets
        /// its <see cref="OperationContext"/> and, in a Host of Modules, the <paramref name="module"/> it runs in, which
        /// routes it to that Module's Unit of Work. Whoever opens an Operation's scope calls it: a Module's Gateway, the
        /// HTTP boundary, or a message delivery. A delivery also passes the <paramref name="traceParent"/> it restored
        /// from the message, the trace context of the Operation that published it (<see cref="Operation.TraceParent"/>).
        /// </summary>
        public void BeginOperation(OperationContext context, Type? module = null, ActivityContext traceParent = default)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(context);
            scope.GetRequiredService<Operation>().Begin(context, module, traceParent);
        }
    }

    // An Operation with no context never runs as an anonymous or system Actor by default: reading it fails.
    internal static OperationContext Read(IServiceProvider services) =>
        services.GetRequiredService<Operation>().Context
        ?? throw new InvalidOperationException(
            "No Operation Context was set for this scope. Send the Operation through a Module's Gateway with an OperationContext, or call BeginOperation on the scope before it runs."
        );
}
