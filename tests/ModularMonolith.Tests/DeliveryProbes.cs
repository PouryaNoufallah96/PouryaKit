using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests;

/// <summary>
/// Stop points inside the Kit's delivery, for Integration Event tests: what an Operation of the test's own flow writes
/// to the Outbox; a hold on the first delivery of one subscription right after its Receipt is recorded, inside its
/// still open transaction; a hold on the first Operation of one Module right after it committed, before anything
/// acknowledges it; a count of deliveries a committed Receipt turned away; and every delivery attempt. Every other
/// Operation and delivery runs as usual.
/// </summary>
internal static class DeliveryProbes
{
    private static readonly AsyncLocal<List<object>?> Written = new();
    private static readonly ConcurrentQueue<(string Subscription, Guid MessageId, DateTimeOffset At)> Attempts = [];
    private static readonly ConcurrentDictionary<string, int> TurnedAway = new(StringComparer.OrdinalIgnoreCase);
    private static Hold? _receiptHold;
    private static Hold? _commitHold;

    /// <summary>
    /// When each delivery of the message <paramref name="messageId"/> to <paramref name="subscription"/> began recording
    /// its Receipt, in any Host of the process: every attempt the store handed out, whether or not its handler then ran.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> AttemptsOf(string subscription, Guid messageId) =>
        [.. Attempts.Where(attempt => attempt.Subscription == subscription && attempt.MessageId == messageId).Select(attempt => attempt.At)];

    /// <summary>Adds every message the commits of this flow write to the Outbox to <paramref name="written"/>, until disposed.</summary>
    public static IDisposable RecordWritten(List<object> written)
    {
        Written.Value = written;
        return new Binding(() => Written.Value = null);
    }

    /// <summary>
    /// Holds the first delivery to <paramref name="subscription"/> that records its Receipt, until released or disposed:
    /// its transaction stays open with the Receipt written and not committed.
    /// </summary>
    public static Hold HoldAfterReceipt(string subscription) =>
        _receiptHold = new Hold(reached => reached is string recordedFor && string.Equals(recordedFor, subscription, StringComparison.OrdinalIgnoreCase), () => _receiptHold = null);

    /// <summary>
    /// Holds the first Operation of <paramref name="module"/> that commits, such as a delivery to it, until released or
    /// disposed: its change and its Receipt are committed, and the delivery is not yet acknowledged.
    /// </summary>
    public static Hold HoldAfterCommit(Type module) =>
        _commitHold = new Hold(reached => reached as Type == module, () => _commitHold = null);

    /// <summary>How many deliveries to <paramref name="subscription"/> found their Receipt already committed.</summary>
    public static int TurnedAwayFrom(string subscription) => TurnedAway.GetValueOrDefault(subscription);

    /// <summary>Wraps the Host's Outbox and Receipts, after <see cref="CommitProbe.Install"/>.</summary>
    public static void Install(IServiceCollection services)
    {
        Decorate<IUnitOfWorkOutbox>(services, (provider, outbox) => new RecordedOutbox(outbox, provider.GetRequiredService<Operation>()));
        Decorate<IReceipts>(services, (_, receipts) => new HeldReceipts(receipts));
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

    public sealed class Hold(Func<object?, bool> matches, Action clear) : IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _taken;

        /// <summary>Completes once the held delivery has reached its stop point and waits.</summary>
        public Task Held => _held.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        public void Release() => _released.TrySetResult();

        /// <summary>Holds nothing more and never releases the held delivery, as a killed process leaves it.</summary>
        public void Abandon() => clear();

        public void Dispose()
        {
            Release();
            clear();
        }

        internal async Task OnReachedAsync(object? reached)
        {
            if (matches(reached) && Interlocked.Exchange(ref _taken, 1) == 0)
            {
                _held.SetResult();
                await _released.Task;
            }
        }
    }

    private sealed class Binding(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class RecordedOutbox(IUnitOfWorkOutbox outbox, Operation operation) : IUnitOfWorkOutbox
    {
        public Task WriteAsync(DbContext dbContext, IReadOnlyList<object> messages, CancellationToken cancellationToken)
        {
            Written.Value?.AddRange(messages);
            return outbox.WriteAsync(dbContext, messages, cancellationToken);
        }

        // Called only after the Unit of Work committed.
        public async Task FlushAsync()
        {
            if (_commitHold is { } hold)
            {
                await hold.OnReachedAsync(operation.Module);
            }

            await outbox.FlushAsync();
        }
    }

    private sealed class HeldReceipts(IReceipts receipts) : IReceipts
    {
        public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken)
        {
            Attempts.Enqueue((subscription, messageId, DateTimeOffset.UtcNow));
            var recorded = await receipts.RecordAsync(subscription, messageId, cancellationToken);
            if (!recorded)
            {
                TurnedAway.AddOrUpdate(subscription, 1, (_, count) => count + 1);
            }
            else if (_receiptHold is { } hold)
            {
                await hold.OnReachedAsync(subscription);
            }

            return recorded;
        }
    }
}
