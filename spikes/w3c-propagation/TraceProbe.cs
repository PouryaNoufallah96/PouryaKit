using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence.EFCore;

namespace W3cPropagation;

/// <summary>What one activity was when it was observed.</summary>
public sealed record Span(
    string Source,
    string Name,
    ActivityKind Kind,
    string Id,
    string TraceId,
    string SpanId,
    string ParentSpanId,
    string? TraceState,
    IReadOnlyList<string> Links,
    IReadOnlyDictionary<string, string?> Tags
)
{
    public const string NoParent = "0000000000000000";

    public static Span Of(Activity activity) =>
        new(
            activity.Source.Name,
            activity.OperationName,
            activity.Kind,
            activity.Id ?? "",
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.ParentSpanId.ToHexString(),
            activity.TraceStateString,
            [.. activity.Links.Select(link => $"{link.Context.TraceId.ToHexString()}/{link.Context.SpanId.ToHexString()}")],
            activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString())
        );

    public string? Tag(string name) => Tags.GetValueOrDefault(name);

    public override string ToString() =>
        $"{Source}:{Name} [{Kind}] trace={TraceId} span={SpanId} parent={ParentSpanId} tracestate={TraceState ?? "<null>"} links=[{string.Join(", ", Links)}] tags={{{string.Join(", ", Tags.Select(tag => $"{tag.Key}={tag.Value}"))}}}";
}

/// <summary>
/// The Spike's observation points, process-wide. The stand-in Operation activity (<see cref="OperationName"/> from
/// <see cref="SourceName"/>) wraps every dispatcher call in a scope, as #47's Operation activity would: outside the Unit of
/// Work, so it is current when the Outbox publishes. The Outbox records the activity current when it writes, and the
/// Receipts record the activity current in the receiving scope right before the subscribed handler runs.
/// </summary>
internal static class TraceProbe
{
    public const string SourceName = "PouryaKit.Spike";
    public const string OperationName = "pouryakit.operation";
    public const string WolverineSourceName = "Wolverine";

    public static readonly ActivitySource Source = new(SourceName);

    public static readonly ConcurrentQueue<Span> Stopped = [];
    public static readonly ConcurrentQueue<(string Host, string? Module, Span? Current, string[] Messages)> Writes = [];
    public static readonly ConcurrentQueue<(string Host, string Subscription, Span? Current)> Receipts = [];

    private static readonly ConcurrentDictionary<string, int> Attempts = new();
    private static string? _failFirst;
    private static (string Subscription, TaskCompletionSource Held, TaskCompletionSource Released)? _holdFirst;

    public static void Clear()
    {
        Stopped.Clear();
        Writes.Clear();
        Receipts.Clear();
        Attempts.Clear();
        _failFirst = null;
        _holdFirst = null;
    }

    /// <summary>
    /// Listens to the stand-in source, and to Wolverine's own <c>ActivitySource</c> only when <paramref name="wolverine"/>
    /// is set; every activity of a listened source is sampled, whether started with a parent context or a parent id.
    /// </summary>
    public static ActivityListener Listen(bool wolverine)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName || (wolverine && source.Name == WolverineSourceName),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Stopped.Enqueue(Span.Of(activity)),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Fails the first delivery attempt to <paramref name="subscription"/> before its Receipt, so it is retried.</summary>
    public static void FailFirst(string subscription) => _failFirst = subscription;

    /// <summary>Holds the first delivery attempt to <paramref name="subscription"/> before its Receipt; returns when it is held.</summary>
    public static (Task Held, Action Release) HoldFirst(string subscription)
    {
        var hold = (subscription, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _holdFirst = hold;
        return (hold.Item2.Task, () => hold.Item3.TrySetResult());
    }

    public static void Install(IServiceCollection services, string hostName)
    {
        Decorate<IDispatcher>(services, (provider, dispatcher) => new OperationActivityDispatcher(dispatcher, provider.GetRequiredService<Operation>()));
        Decorate<IUnitOfWorkOutbox>(services, (provider, outbox) => new ObservedOutbox(outbox, provider.GetRequiredService<Operation>(), hostName));
        Decorate<IReceipts>(services, (_, receipts) => new ObservedReceipts(receipts, hostName));
    }

    private static void Decorate<TService>(IServiceCollection services, Func<IServiceProvider, TService, TService> decorate)
        where TService : class
    {
        var descriptor = services.Single(descriptor => descriptor.ServiceType == typeof(TService));
        services.Remove(descriptor);
        services.AddScoped(provider =>
            decorate(
                provider,
                descriptor.ImplementationFactory is { } factory
                    ? (TService)factory(provider)
                    : (TService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)
            )
        );
    }

    private static Span? Current() => Activity.Current is { } current ? Span.Of(current) : null;

    // The stand-in for #47's Operation activity: one per scope's outermost dispatcher call; a nested command joins it.
    private sealed class OperationActivityDispatcher(IDispatcher inner, Operation operation) : IDispatcher
    {
        private int _depth;

        public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default) =>
            RunAsync(command, () => inner.SendAsync(command, cancellationToken));

        public Task<Result<TResponse>> SendAsync<TResponse>(Command<TResponse> command, CancellationToken cancellationToken = default)
            where TResponse : notnull => RunAsync(command, () => inner.SendAsync(command, cancellationToken));

        public Task<Result<TResponse>> QueryAsync<TResponse>(Query<TResponse> query, CancellationToken cancellationToken = default)
            where TResponse : notnull => RunAsync(query, () => inner.QueryAsync(query, cancellationToken));

        private async Task<T> RunAsync<T>(object request, Func<Task<T>> run)
        {
            if (_depth++ > 0)
            {
                try
                {
                    return await run();
                }
                finally
                {
                    _depth--;
                }
            }

            using var activity = Source.StartActivity(OperationName);
            activity?.SetTag("pouryakit.module", operation.Module?.Name);
            activity?.SetTag("pouryakit.operation", request.GetType().Name);
            try
            {
                return await run();
            }
            finally
            {
                _depth--;
            }
        }
    }

    private sealed class ObservedOutbox(IUnitOfWorkOutbox outbox, Operation operation, string host) : IUnitOfWorkOutbox
    {
        public Task WriteAsync(
            DbContext dbContext,
            IReadOnlyList<object> messages,
            IReadOnlyList<EntityEntry> changedAggregates,
            CancellationToken cancellationToken
        )
        {
            Writes.Enqueue((host, operation.Module?.Name, Current(), [.. messages.Select(message => message.GetType().Name)]));
            return outbox.WriteAsync(dbContext, messages, changedAggregates, cancellationToken);
        }

        public Task FlushAsync() => outbox.FlushAsync();
    }

    private sealed class ObservedReceipts(IReceipts receipts, string host) : IReceipts
    {
        public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken)
        {
            Receipts.Enqueue((host, subscription, Current()));
            var attempt = Attempts.AddOrUpdate(subscription, 1, (_, count) => count + 1);
            if (attempt == 1 && string.Equals(_failFirst, subscription, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The Spike fails the first delivery to {subscription}.");
            }

            if (attempt == 1 && _holdFirst is { } hold && string.Equals(hold.Subscription, subscription, StringComparison.OrdinalIgnoreCase))
            {
                hold.Held.TrySetResult();
                await hold.Released.Task;
            }

            return await receipts.RecordAsync(subscription, messageId, cancellationToken);
        }

        public Task<bool> RecordVersionAsync(string subscription, string aggregateId, long version, CancellationToken cancellationToken) =>
            receipts.RecordVersionAsync(subscription, aggregateId, version, cancellationToken);
    }
}
