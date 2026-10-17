using Grpc.Core;
using Grpc.Core.Interceptors;
using PouryaKit.Web;

namespace PouryaKit.Grpc;

// Writes each call's Operation Context as the pairs of Web's one codec, replacing any header of the same name, and gives
// a call without a deadline of its own the client's. It reads no incoming request.
internal sealed class OperationContextInterceptor(TimeSpan deadline, TimeProvider time) : Interceptor
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

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation
    ) => continuation(request, WithContext(context));

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation
    ) => continuation(WithContext(context));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation
    ) => continuation(WithContext(context));

    private ClientInterceptorContext<TRequest, TResponse> WithContext<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> call)
        where TRequest : class
        where TResponse : class
    {
        var operation =
            call.Options.Headers is { } given && OperationContextCallOptions.Contexts.TryGetValue(given, out var found)
                ? found
                : throw new InvalidOperationException(
                    $"The gRPC call {call.Method.FullName} has no Operation Context, and its client sends one with every call. Give it the calling Operation's context with WithOperationContext(context) on its CallOptions."
                );
        var pairs = OperationContextHttpHeaders.Pairs(operation);
        Metadata headers = [.. given.Where(entry => !pairs.Any(pair => pair.Key == entry.Key))];
        foreach (var (name, value) in pairs)
        {
            headers.Add(name, value);
        }

        var options = call.Options.WithHeaders(headers);
        return new(call.Method, call.Host, options.Deadline is null ? options.WithDeadline(time.GetUtcNow().UtcDateTime + deadline) : options);
    }
}
