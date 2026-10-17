using System.Runtime.CompilerServices;
using Grpc.Core;
using PouryaKit.Cqrs;

namespace PouryaKit.Grpc;

/// <summary>Gives one gRPC call the Operation Context its client's interceptor sends.</summary>
public static class OperationContextCallOptions
{
    // The context travels with the call's own metadata instance, as an HTTP request's travels in its options: a call's
    // CallOptions hold nothing else of the caller's, and the client is shared by every Operation that calls it.
    internal static readonly ConditionalWeakTable<Metadata, OperationContext> Contexts = [];

    extension(CallOptions options)
    {
        /// <summary>
        /// Gives the call the calling Operation's <paramref name="context"/>, which a client registered with
        /// <c>AddPouryaKitGrpcClient</c> sends as Web's Kit headers in the call's metadata: the Actor, the tenant, the
        /// correlation, and the calling Operation's <c>OperationId</c> as the causation. A Kit Host that trusts this one
        /// as a peer continues the Operation from them. The call's other headers are kept.
        /// </summary>
        public CallOptions WithOperationContext(OperationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            Metadata headers = [.. options.Headers ?? []];
            Contexts.AddOrUpdate(headers, context);
            return options.WithHeaders(headers);
        }
    }
}
