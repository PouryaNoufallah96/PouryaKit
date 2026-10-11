using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using PouryaKit.Observability;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A test's listeners, until disposed: every activity of the Kit's Operation source and of <see cref="Upstream"/> that
/// stopped, also Wolverine's when asked, all sampled with all data; and every Operation duration measurement with its
/// tags. A test may leave out either listener. No exporter: what the Host emits is what the listeners see.
/// </summary>
internal sealed class RecordedTelemetry : IDisposable
{
    /// <summary>The test's own source, for the activity that stands in for an incoming request.</summary>
    public static readonly ActivitySource Upstream = new(UpstreamName);

    // By name: a listener added while the static source is first created would never be asked about it.
    private const string UpstreamName = "ModularMonolith.Tests";

    private readonly ActivityListener _activities;
    private readonly MeterListener _durations = new();
    private readonly ConcurrentQueue<Activity> _stopped = [];
    private readonly ConcurrentQueue<Duration> _measured = [];

    public RecordedTelemetry(bool wolverine = false, bool activities = true, bool durations = true)
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name is OperationTelemetry.SourceName or UpstreamName || (wolverine && source.Name == "Wolverine"),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            // Wolverine starts its spans with a string parent id, sampled here; without it they would not exist.
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        if (activities)
        {
            ActivitySource.AddActivityListener(_activities);
        }

        _durations.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OperationTelemetry.SourceName && instrument.Name == OperationTelemetry.DurationName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _durations.SetMeasurementEventCallback<double>(
            (_, seconds, tags, _) =>
            {
                Dictionary<string, object?> named = [];
                foreach (var (name, value) in tags)
                {
                    named[name] = value;
                }

                _measured.Enqueue(new Duration(seconds, named));
            }
        );
        if (durations)
        {
            _durations.Start();
        }
    }

    /// <summary>Every recorded activity of <paramref name="trace"/> that stopped.</summary>
    public IReadOnlyList<Activity> Of(ActivityTraceId trace) => [.. _stopped.Where(activity => activity.TraceId == trace)];

    /// <summary>Every Operation duration measured, with its tags.</summary>
    public IReadOnlyList<Duration> Durations => [.. _measured];

    public void Dispose()
    {
        _activities.Dispose();
        _durations.Dispose();
    }

    public sealed record Duration(double Seconds, IReadOnlyDictionary<string, object?> Tags);
}
