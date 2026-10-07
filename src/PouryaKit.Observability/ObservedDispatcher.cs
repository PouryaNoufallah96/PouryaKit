using System.Diagnostics;
using System.Diagnostics.Metrics;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Observability;

// Wraps the scope's dispatcher, so the activity encloses the whole pipeline, the Unit of Work included: it is current
// while the Unit of Work writes the Outbox, where Wolverine takes it as each envelope's parent-id (ADR 0009). A handler
// sends a nested command through the same scoped dispatcher, so its activity is a child of the one current there.
internal sealed class ObservedDispatcher(IDispatcher dispatcher, Operation operation) : IDispatcher
{
    private static readonly ActivitySource Source = new(OperationTelemetry.SourceName);
    private static readonly Meter Meter = new(OperationTelemetry.SourceName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        OperationTelemetry.DurationName,
        unit: "s",
        description: "The duration of a command or query the dispatcher ran."
    );

    public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ObserveAsync(command, () => dispatcher.SendAsync(command, cancellationToken));
    }

    public Task<Result<TResponse>> SendAsync<TResponse>(Command<TResponse> command, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        return ObserveAsync(command, () => dispatcher.SendAsync(command, cancellationToken));
    }

    public Task<Result<TResponse>> QueryAsync<TResponse>(Query<TResponse> query, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        return ObserveAsync(query, () => dispatcher.QueryAsync(query, cancellationToken));
    }

    private async Task<TResult> ObserveAsync<TResult>(object commandOrQuery, Func<Task<TResult>> dispatch)
        where TResult : Result
    {
        if (!Source.HasListeners() && !Duration.Enabled)
        {
            return await dispatch();
        }

        TagList tags = new() { { OperationTelemetry.OperationTag, commandOrQuery.GetType().Name } };
        if (operation.Module is { } module)
        {
            tags.Add(OperationTelemetry.ModuleTag, module.Name);
        }

        if (operation.Context is { } context)
        {
            tags.Add(OperationTelemetry.ActorKindTag, context.Actor.Kind.ToString());
        }

        // A delivery's parent is Wolverine's span of the envelope when one is current; with none of that trace, the
        // publishing Operation's context the delivery restored. A default parent means the current activity.
        var restored = operation.TraceParent;
        var parent = restored != default && Activity.Current?.TraceId != restored.TraceId ? restored : default;
        using var activity = Source.StartActivity(OperationTelemetry.ActivityName, ActivityKind.Internal, parent, tags);
        if (activity is not null && restored.TraceState is { } traceState && activity.TraceId == restored.TraceId)
        {
            activity.TraceStateString = traceState;
        }

        var started = Stopwatch.GetTimestamp();
        var outcome = OperationTelemetry.Threw;
        try
        {
            var result = await dispatch();
            if (result.IsSuccess)
            {
                outcome = OperationTelemetry.Succeeded;
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                outcome = OperationTelemetry.Failed;
                activity?.SetStatus(ActivityStatusCode.Error, string.Join(", ", result.Errors.Select(error => error.Code)));
            }

            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().FullName);
            activity?.AddException(exception);
            throw;
        }
        finally
        {
            tags.Add(OperationTelemetry.OutcomeTag, outcome);
            activity?.SetTag(OperationTelemetry.OutcomeTag, outcome);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
