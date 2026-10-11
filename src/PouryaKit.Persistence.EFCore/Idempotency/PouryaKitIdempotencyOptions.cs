namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// How long the Host keeps the keys of <c>[Idempotent]</c> commands. Every <see cref="CleanupInterval"/>, one background
/// service removes from every Module's <see cref="IdempotencyTable"/> the keys reserved longer than
/// <see cref="Retention"/> ago, <see cref="BatchSize"/> at a time, each batch its own statement on a connection of its
/// own, outside every request and Unit of Work. A repeat within the retention replays its first success; one after its
/// key was removed runs as a new request. Configure it with <c>Configure&lt;PouryaKitIdempotencyOptions&gt;</c>, for
/// example from a configuration section.
/// <c>AddPouryaKitEfCore</c> installs the service in every Host: each cleanup walks every DbContext that call made a
/// Unit of Work, probes its schema with PostgreSQL's <c>to_regclass</c>, and returns silently for one with no table, a
/// Module with no <c>[Idempotent]</c> command. It runs PostgreSQL SQL only (<c>to_regclass</c>, <c>ctid</c>), as the
/// store and the table are PostgreSQL-only.
/// </summary>
/// <remarks>
/// The Host fails when it starts unless <see cref="Retention"/> is positive and at most 36500 days, or
/// <see cref="Timeout.InfiniteTimeSpan"/>, <see cref="CleanupInterval"/> is from 1 ms to about 49.7 days (the range of
/// <see cref="PeriodicTimer"/>), and <see cref="BatchSize"/> is at least 1.
/// </remarks>
public sealed class PouryaKitIdempotencyOptions
{
    /// <summary>
    /// How long a key is kept after it was reserved: 24 hours by default. A key is removed by the first cleanup after
    /// its retention ends. <see cref="Timeout.InfiniteTimeSpan"/> keeps every key, and no cleanup runs. The time is
    /// taken when the reservation starts, before any wait on a concurrent holder of the same key, so keep the retention
    /// well above the longest request: a retention shorter than a request transaction is not supported.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The time between two cleanups, on the Host's <see cref="TimeProvider"/>: 1 hour by default.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The most keys one statement removes, 1000 by default; a cleanup repeats it until a batch is short.</summary>
    public int BatchSize { get; set; } = 1000;
}
