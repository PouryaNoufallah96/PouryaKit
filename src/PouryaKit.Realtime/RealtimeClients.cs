using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace PouryaKit.Realtime;

// The clients connected to this instance, keyed by tenant and Channel. It holds no event: every instance's listener
// receives every push, so a client is served by the instance it is connected to and by no other. The listener writes
// on the thread that reads notifications, without waiting, into each client's bounded buffer: a client a full buffer
// behind is disconnected once its buffer has drained, the overflow event and every later one dropped, and its
// browser's EventSource reconnects and re-reads the state.
internal sealed class RealtimeClients
{
    private const int Buffered = 256;

    private static readonly SseItem<string> ConnectedEvent = new("", RealtimeEvents.Connected);
    private static readonly SseItem<string> GapEvent = new("", RealtimeEvents.Gap);

    private readonly Lock _lock = new();
    private readonly Dictionary<(string Tenant, string Channel), List<ChannelWriter<SseItem<string>>>> _listening = [];
    private readonly HashSet<ChannelWriter<SseItem<string>>> _connected = [];

    /// <summary>
    /// One client's events: <see cref="RealtimeEvents.Connected"/> once it is registered, then each event pushed for
    /// <paramref name="tenant"/> on one of <paramref name="channels"/>, until it is cancelled or falls a full buffer behind.
    /// </summary>
    public async IAsyncEnumerable<SseItem<string>> ConnectAsync(
        string tenant,
        IReadOnlyCollection<string> channels,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var buffer = Channel.CreateBounded<SseItem<string>>(new BoundedChannelOptions(Buffered) { SingleReader = true });
        Add(tenant, channels, buffer.Writer);
        try
        {
            yield return ConnectedEvent;
            while (true)
            {
                try
                {
                    if (!await buffer.Reader.WaitToReadAsync(cancellationToken))
                    {
                        yield break;
                    }
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                while (buffer.Reader.TryRead(out var pushed))
                {
                    yield return pushed;
                }
            }
        }
        finally
        {
            Remove(tenant, channels, buffer.Writer);
        }
    }

    /// <summary>Passes a push, as the listener read it, to each client of its tenant that listens on its Channel.</summary>
    public void Push(RealtimeNotification notification)
    {
        lock (_lock)
        {
            if (_listening.TryGetValue((notification.Tenant, notification.Channel), out var clients))
            {
                SseItem<string> pushed = new(((JsonElement)notification.Body).GetRawText(), notification.Channel);
                foreach (var client in clients)
                {
                    Write(client, pushed);
                }
            }
        }
    }

    /// <summary>Sends <see cref="RealtimeEvents.Gap"/> to every connected client.</summary>
    public void SendGap()
    {
        lock (_lock)
        {
            foreach (var client in _connected)
            {
                Write(client, GapEvent);
            }
        }
    }

    private static void Write(ChannelWriter<SseItem<string>> client, SseItem<string> item)
    {
        if (!client.TryWrite(item))
        {
            client.TryComplete();
        }
    }

    private void Add(string tenant, IReadOnlyCollection<string> channels, ChannelWriter<SseItem<string>> client)
    {
        lock (_lock)
        {
            _connected.Add(client);
            foreach (var channel in channels)
            {
                (_listening.TryGetValue((tenant, channel), out var clients) ? clients : _listening[(tenant, channel)] = []).Add(client);
            }
        }
    }

    private void Remove(string tenant, IReadOnlyCollection<string> channels, ChannelWriter<SseItem<string>> client)
    {
        lock (_lock)
        {
            _connected.Remove(client);
            foreach (var channel in channels)
            {
                if (_listening.TryGetValue((tenant, channel), out var clients) && clients.Remove(client) && clients.Count == 0)
                {
                    _listening.Remove((tenant, channel));
                }
            }
        }
    }
}
