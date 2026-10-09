using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A stop point between the Unit of Work's commit and the moment it sends what it wrote to the Outbox, for the
/// Operations a test sends from its own flow: the probe runs there, and the Notifications are sent only if it returns
/// true. Every other Operation, such as a delivery, commits as usual.
/// </summary>
internal static class CommitProbe
{
    private static readonly AsyncLocal<Func<Task<bool>>?> Current = new();

    /// <summary>Runs <paramref name="probe"/> after each commit in this flow, until disposed.</summary>
    public static IDisposable AfterCommit(Func<Task<bool>> probe)
    {
        Current.Value = probe;
        return new Binding();
    }

    /// <summary>Wraps the Host's Outbox so its flush passes through the probe.</summary>
    public static void Install(IServiceCollection services)
    {
        var outbox = services.Single(descriptor => descriptor.ServiceType == typeof(IUnitOfWorkOutbox));
        services.Remove(outbox);
        services.AddScoped<IUnitOfWorkOutbox>(provider => new ProbedOutbox(
            (IUnitOfWorkOutbox)ActivatorUtilities.CreateInstance(provider, outbox.ImplementationType!)
        ));
    }

    private sealed class Binding : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }

    private sealed class ProbedOutbox(IUnitOfWorkOutbox outbox) : IUnitOfWorkOutbox
    {
        public Task WriteAsync(
            DbContext dbContext,
            IReadOnlyList<object> messages,
            IReadOnlyList<EntityEntry> changedAggregates,
            CancellationToken cancellationToken
        ) => outbox.WriteAsync(dbContext, messages, changedAggregates, cancellationToken);

        public async Task FlushAsync()
        {
            if (Current.Value is not { } probe || await probe())
            {
                await outbox.FlushAsync();
            }
        }
    }
}
