using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests;

/// <summary>
/// Stop points inside the Kit's delivery, for Integration Event tests: what an Operation of the test's own flow writes
/// to the Outbox, and a hold on the first delivery of one subscription right after its Receipt is recorded, inside its
/// still open transaction. Every other Operation and delivery runs as usual. Every delivery attempt is recorded.
/// </summary>
internal static class DeliveryProbes
{
    private static readonly AsyncLocal<List<object>?> Written = new();
    private static readonly ConcurrentQueue<(string Subscription, Guid MessageId, DateTimeOffset At)> Attempts = [];
    private static ReceiptHold? _hold;

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
    public static ReceiptHold HoldAfterReceipt(string subscription) => _hold = new ReceiptHold(subscription);

    /// <summary>Wraps the Host's Outbox and Receipts, after <see cref="CommitProbe.Install"/>.</summary>
    public static void Install(IServiceCollection services)
    {
        Decorate<IUnitOfWorkOutbox>(services, outbox => new RecordedOutbox(outbox));
        Decorate<IReceipts>(services, receipts => new HeldReceipts(receipts));
    }

    private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> decorate)
        where TService : class
    {
        var descriptor = services.Single(descriptor => descriptor.ServiceType == typeof(TService));
        services.Remove(descriptor);
        services.AddScoped(provider =>
            decorate(
                descriptor.ImplementationFactory is { } factory
                    ? (TService)factory(provider)
                    : (TService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)
            )
        );
    }

    public sealed class ReceiptHold(string subscription) : IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _taken;

        /// <summary>Completes once the held delivery has recorded its Receipt and waits.</summary>
        public Task Held => _held.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        public void Release() => _released.TrySetResult();

        /// <summary>Holds nothing more and never releases the held delivery, as a killed process leaves it.</summary>
        public void Abandon() => _hold = null;

        public void Dispose()
        {
            Release();
            _hold = null;
        }

        internal async Task OnRecordedAsync(string recordedFor)
        {
            if (string.Equals(recordedFor, subscription, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _taken, 1) == 0)
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

    private sealed class RecordedOutbox(IUnitOfWorkOutbox outbox) : IUnitOfWorkOutbox
    {
        public Task WriteAsync(DbContext dbContext, IReadOnlyList<object> messages, CancellationToken cancellationToken)
        {
            Written.Value?.AddRange(messages);
            return outbox.WriteAsync(dbContext, messages, cancellationToken);
        }

        public Task FlushAsync() => outbox.FlushAsync();
    }

    private sealed class HeldReceipts(IReceipts receipts) : IReceipts
    {
        public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken)
        {
            Attempts.Enqueue((subscription, messageId, DateTimeOffset.UtcNow));
            var recorded = await receipts.RecordAsync(subscription, messageId, cancellationToken);
            if (recorded && _hold is { } hold)
            {
                await hold.OnRecordedAsync(subscription);
            }

            return recorded;
        }
    }
}
