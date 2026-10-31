using Grpc.Core;
using Grpc.Core.Interceptors;
using PouryaKit.Web;

namespace PouryaKit.Grpc;

// Writes each call's Operation Context as the pairs of Web's one codec, in place of every Kit header the call had, and
// gives a call without a deadline of its own the client's, measured on the system clock: gRPC judges a deadline against
// real UTC, so the Operation's replaceable TimeProvider plays no part here. A deadline later than DateTime.MaxValue is
// DateTime.MaxValue, which gRPC sends as none. It reads no incoming request.
internal sealed class OperationContextInterceptor(TimeSpan deadline) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation
    ) => continuation(request, WithContext(context));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation
    ) => continuation(request, WithContext(context));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<
        TRequest,
        TResponse
    >(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation
    ) => continuation(request, WithContext(context));

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<
        TRequest,
        TResponse
    >(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation
    ) => continuation(WithContext(context));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<
        TRequest,
        TResponse
    >(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation
    ) => continuation(WithContext(context));

    private ClientInterceptorContext<TRequest, TResponse> WithContext<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> call
    )
        where TRequest : class
        where TResponse : class
    {
        var operation =
            call.Options.Headers is { } given
            && OperationContextCallOptions.Contexts.TryGetValue(given, out var found)
                ? found
                : throw new InvalidOperationException(
                    $"The gRPC call {call.Method.FullName} has no Operation Context, and its client sends one with every call. Give it the calling Operation's context with WithOperationContext(context) on its CallOptions."
                );
        // Every Kit header goes first, as Web's outgoing handler does, so an earlier tenant never survives beside a context
        // without one.
        Metadata headers =
        [
            .. given.Where(entry => !OperationContextHttpHeaders.Names.Contains(entry.Key)),
        ];
        foreach (var (name, value) in OperationContextHttpHeaders.Pairs(operation))
        {
            headers.Add(name, value);
        }

        var options = call.Options.WithHeaders(headers);
        if (options.Deadline is null)
        {
            var now = DateTime.UtcNow;
            options = options.WithDeadline(
                deadline < DateTime.MaxValue - now ? now + deadline : DateTime.MaxValue
            );
        }

        return new(call.Method, call.Host, options);
    }
}
