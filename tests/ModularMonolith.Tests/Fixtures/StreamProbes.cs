using Marten;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Persistence.Marten;
using IChangeSet = Marten.Services.IChangeSet;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A Marten store listener: it runs before each save's batch, and after Marten's commit, before every listener of the
/// session, Wolverine's send included, so a test reads the store between the commit and the send there.
/// </summary>
internal sealed class StreamCommitProbe : DocumentSessionListenerBase
{
    public Func<Task>? OnBeforeSave { get; set; }

    public Func<Task>? OnCommit { get; set; }

    public static void Install(IServiceCollection services, StreamCommitProbe probe) =>
        services.ConfigureMarten(options => options.Listeners.Add(probe));

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (OnBeforeSave is { } onBeforeSave)
        {
            await onBeforeSave();
        }
    }

    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        if (OnCommit is { } onCommit)
        {
            await onCommit();
        }
    }
}

/// <summary>
/// Makes Wolverine's send after Marten's commit fail, for every Operation of the Host: the Outbox's write takes the
/// listener that sends out of the session and puts one that throws in its place.
/// </summary>
internal static class FailingStreamSend
{
    public static void Install(IServiceCollection services)
    {
        var outbox = services.Single(descriptor => descriptor.ServiceType == typeof(IStreamOutbox));
        services.Remove(outbox);
        services.AddScoped<IStreamOutbox>(provider => new Failing((IStreamOutbox)outbox.ImplementationFactory!(provider)));
    }

    private sealed class Failing(IStreamOutbox outbox) : IStreamOutbox
    {
        public async Task WriteAsync(IDocumentSession session, IReadOnlyList<object> messages, CancellationToken cancellationToken)
        {
            await outbox.WriteAsync(session, messages, cancellationToken);
            // None when nothing the Operation published has a subscription.
            if (session.Listeners.SingleOrDefault(listener => listener.GetType().Name == "FlushOutgoingMessagesOnCommit") is { } send)
            {
                session.Listeners[session.Listeners.IndexOf(send)] = new Throwing();
            }
        }
    }

    private sealed class Throwing : DocumentSessionListenerBase
    {
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token) =>
            throw new InvalidOperationException("The send after the commit failed.");
    }
}
