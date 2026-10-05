using System.Collections.Concurrent;
using Npgsql;

namespace S2.Kit.Harness;

// Spike observation harness, not Kit code: it lets a test script a handler's outcome and see every invocation.

public enum FailureKind { FailedResult, Throw }

/// <summary>A handler fails its first <see cref="Failures"/> invocations for one product, then succeeds.</summary>
public sealed record Plan(FailureKind Kind, int Failures)
{
    public static Plan FailForever(FailureKind kind = FailureKind.FailedResult) => new(kind, int.MaxValue);
}

public static class HandlerScript
{
    private static readonly ConcurrentDictionary<(string Handler, Guid ProductId), Plan> Plans = new();
    private static readonly ConcurrentDictionary<(string Handler, Guid ProductId), int> Invocations = new();

    public static void Set(string handler, Guid productId, Plan plan) => Plans[(handler, productId)] = plan;

    /// <summary>The outcome of this invocation: null to succeed, otherwise how to fail.</summary>
    public static FailureKind? Next(string handler, Guid productId)
    {
        var n = Invocations.AddOrUpdate((handler, productId), 1, (_, i) => i + 1);
        return Plans.TryGetValue((handler, productId), out var plan) && n <= plan.Failures ? plan.Kind : null;
    }
}

/// <summary>
/// Records every handler invocation in <c>spike.deliveries</c>, on its own connection, before the outcome. Registered
/// scoped, so <see cref="ScopeId"/> identifies the scope the invocation ran in.
/// </summary>
public sealed class DeliveryLog(NpgsqlDataSource db)
{
    public Guid ScopeId { get; } = Guid.CreateVersion7();

    /// <summary>The handler body every Spike handler shares: log the invocation, then do what the script says.</summary>
    public async Task<Result> RunAsync(string handler, Guid productId, Guid eventId)
    {
        var failure = HandlerScript.Next(handler, productId);
        await using var cmd = db.CreateCommand(
            "insert into spike.deliveries(handler, product_id, event_id, outcome, scope_id) values ($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(handler);
        cmd.Parameters.AddWithValue(productId);
        cmd.Parameters.AddWithValue(eventId);
        cmd.Parameters.AddWithValue(failure?.ToString() ?? "Succeeded");
        cmd.Parameters.AddWithValue(ScopeId);
        await cmd.ExecuteNonQueryAsync();
        return failure switch
        {
            null => Result.Success(),
            FailureKind.FailedResult => Result.Failure($"{handler} scripted failure"),
            _ => throw new InvalidOperationException($"{handler} scripted exception"),
        };
    }
}
