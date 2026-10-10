using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Every subscription of one Host, of Notification Handlers and Integration Event Handlers, every Integration Event
/// and Snapshot its Modules publish, and every one it receives from another Host. It lives in the Host's service
/// collection, not in a static, so every Module's declaration adds to the same one, whenever it runs, and two Hosts in one
/// process never share it. Names are compared ignoring case, because local queue names are lower-cased.
/// </summary>
internal sealed class Subscriptions : IWireNames
{
    // The local queues Wolverine 6.46.0 creates for itself (LocalTransport, TransportConstants): "agents" is held in
    // memory, and none of them is a subscription's durable queue. DurableQueuesCheck verifies the queues Wolverine built, so a
    // name a later Wolverine reserves fails the Host's start too.
    private static readonly FrozenSet<string> ReservedQueues = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "agents",
        "default",
        "durable",
        "replies",
        "scheduled",
        "system"
    );

    private readonly Dictionary<string, Subscription> _byName = new(StringComparer.OrdinalIgnoreCase);
    // The wire name of every Domain Event delivered as a Notification and of every Integration Event published.
    private readonly Dictionary<string, Type> _typeByWireName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, Publication> _publications = [];
    // Every published Snapshot contract, by its type.
    private readonly Dictionary<Type, SnapshotPublication> _snapshots = [];
    // Every Integration Event and Snapshot another Host publishes, by its type, with its wire name and whether it is a Snapshot.
    private readonly Dictionary<Type, (string WireName, bool IsSnapshot)> _received = [];

    public IEnumerable<Subscription> All => _byName.Values;

    public IEnumerable<Type> MessageTypes => _byName.Values.Select(subscription => subscription.Message).Distinct();

    public IReadOnlyDictionary<Type, Publication> Publications => _publications;

    public IEnumerable<SnapshotPublication> Snapshots => _snapshots.Values;

    public bool IsSnapshot(Type message) => _snapshots.ContainsKey(message) || _received.GetValueOrDefault(message).IsSnapshot;

    /// <summary>Every Integration Event and Snapshot this Host receives from another Host.</summary>
    public IReadOnlySet<Type> ReceivedTypes => _received.Keys.ToHashSet();

    /// <summary>The wire name of every Integration Event and Snapshot that has one in this Host: published or received.</summary>
    public IReadOnlyDictionary<Type, string> WireNames =>
        _publications
            .Select(publication => KeyValuePair.Create(publication.Key, publication.Value.WireName))
            .Concat(_received.Select(received => KeyValuePair.Create(received.Key, received.Value.WireName)))
            .ToDictionary();

    IReadOnlyDictionary<Type, string> IWireNames.Published =>
        _publications.ToDictionary(publication => publication.Key, publication => publication.Value.WireName);

    IReadOnlyCollection<string> IWireNames.Received =>
        [.. MessageTypes.Where(_received.ContainsKey).Select(type => _received[type].WireName)];

    public static Subscriptions In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(Subscriptions)) is { } existing)
        {
            return (Subscriptions)existing.ImplementationInstance!;
        }

        var subscriptions = new Subscriptions();
        services.AddSingleton(subscriptions);
        services.AddSingleton<IWireNames>(subscriptions);
        return subscriptions;
    }

    public Subscription Named(string name) =>
        _byName.TryGetValue(name, out var subscription) ? subscription : throw new UnknownSubscriptionException(name);

    /// <summary>
    /// Adds every subscription, or none: throws, naming each problem, when a name is missing or invalid, or equal
    /// ignoring case to one another subscription or message type already has. Declaring the same handler again adds
    /// nothing when it belongs to the same Module.
    /// </summary>
    public void Add(IReadOnlyList<Subscription> subscriptions, List<string> problems)
    {
        Dictionary<string, Subscription> byName = new(_byName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Type> typeByWireName = new(_typeByWireName, StringComparer.OrdinalIgnoreCase);
        foreach (var subscription in subscriptions)
        {
            // An Integration Event's wire name is declared where it is published. A derived subscription name starts
            // with the Domain Event's wire name, so a bad wire name is reported once, for itself.
            if (subscription.IsNotification)
            {
                var wireName = MessageNames.WireNameOf(subscription.Message);
                if (!Check(wireName, $"{subscription.Message.FullName}'s wire name", isQueue: false, problems))
                {
                    continue;
                }

                if (!typeByWireName.TryAdd(wireName, subscription.Message) && typeByWireName[wireName] != subscription.Message)
                {
                    problems.Add(
                        $"{subscription.Message.FullName} and {typeByWireName[wireName].FullName} have the wire name {wireName}, ignoring case; give one a WireName of its own."
                    );
                }
            }

            if (!Check(subscription.Name, $"{subscription.Handler.FullName}'s subscription name", isQueue: true, problems))
            {
                continue;
            }

            if (byName.TryGetValue(subscription.Name, out var existing))
            {
                if (existing.Handler != subscription.Handler || existing.Message != subscription.Message)
                {
                    problems.Add(
                        $"{subscription.Handler.FullName} and {existing.Handler.FullName} have the subscription name {subscription.Name}, ignoring case; {(subscription.IsNotification ? "give one a SubscriptionName of its own" : "subscribe one under another name")}."
                    );
                }
                else if (existing.Module != subscription.Module)
                {
                    problems.Add(
                        $"The subscription {subscription.Name} belongs to {existing.Module?.Name ?? "the application"} and to {subscription.Module?.Name ?? "the application"}; one Module owns its deliveries."
                    );
                }

                continue;
            }

            byName.Add(subscription.Name, subscription);
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        foreach (var (name, subscription) in byName)
        {
            _byName.TryAdd(name, subscription);
        }

        foreach (var (wireName, type) in typeByWireName)
        {
            _typeByWireName.TryAdd(wireName, type);
        }
    }

    /// <summary>
    /// Records that <paramref name="module"/> publishes <paramref name="integrationEvent"/> under
    /// <paramref name="wireName"/>. Throws when the type is not a concrete class or is a Domain Event, when the name is
    /// missing, invalid or equal ignoring case to another type's, and when the type is already published under another
    /// name or by another Module. Declaring the same publication again adds nothing.
    /// </summary>
    public void Publish(Type integrationEvent, string wireName, Type? module) =>
        Publish(integrationEvent, wireName, module, snapshot: null);

    /// <summary>
    /// Records that <paramref name="module"/> publishes the Snapshot of <paramref name="snapshot"/>'s aggregate under
    /// <paramref name="wireName"/>, mapped by its mapper: with the checks of <see cref="Publish(Type, string, Type?)"/>,
    /// and refused when the type is already published as an Integration Event or as another declaration's Snapshot.
    /// </summary>
    public void PublishSnapshot(SnapshotPublication snapshot, string wireName, Type? module) =>
        Publish(snapshot.Snapshot, wireName, module, snapshot);

    /// <summary>
    /// Records that another Host publishes <paramref name="message"/> under <paramref name="wireName"/>, as a Snapshot
    /// when <paramref name="isSnapshot"/>, so this Host's subscriptions to it receive it from there. Throws when the
    /// type is not a concrete class or is a Domain Event, when the name is missing, invalid or equal ignoring case to
    /// another type's, when the type is already received under another name or as the other kind, and when a Module of
    /// this Host publishes it. Declaring the same again adds nothing.
    /// </summary>
    public void Receive(Type message, string wireName, bool isSnapshot)
    {
        List<string> problems = [];
        if (message.IsAssignableTo(typeof(DomainEvent)))
        {
            problems.Add(
                $"{message.FullName} is a Domain Event; it reaches its own Module's Notification Handlers and never another Host. Receive the Integration Event its Module publishes."
            );
        }
        else if (!message.IsClass || message.IsAbstract || message.ContainsGenericParameters)
        {
            problems.Add($"{message.FullName} is not a concrete class; receive the concrete Integration Event or Snapshot type.");
        }
        else if (Check(wireName, $"{message.FullName}'s wire name", isQueue: false, problems))
        {
            if (_received.TryGetValue(message, out var received))
            {
                if (received != (wireName, isSnapshot))
                {
                    problems.Add(
                        $"{message.FullName} is received as {Kind(received.IsSnapshot)} {received.WireName} and as {Kind(isSnapshot)} {wireName}; another Host publishes it under one wire name, as one kind."
                    );
                }

                ThrowIfAny(problems);
                return;
            }

            if (_publications.TryGetValue(message, out var published))
            {
                problems.Add(Both(message, published.Module));
            }
            else if (_typeByWireName.TryGetValue(wireName, out var named))
            {
                problems.Add(
                    $"{message.FullName} and {named.FullName} have the wire name {wireName}, ignoring case; one wire name is one version of one contract type."
                );
            }
        }

        ThrowIfAny(problems);
        _received.Add(message, (wireName, isSnapshot));
        _typeByWireName.Add(wireName, message);
    }

    private static string Kind(bool isSnapshot) => isSnapshot ? "the Snapshot" : "the Integration Event";

    private static string Both(Type message, Type? module) =>
        $"{message.FullName} is published by {module?.Name ?? "the application"} in this Host and declared as received from another Host. A Host publishes a contract or receives it: its subscriptions here receive it from its publication, so remove the Receive declaration.";

    private void Publish(Type integrationEvent, string wireName, Type? module, SnapshotPublication? snapshot)
    {
        List<string> problems = [];
        if (integrationEvent.IsAssignableTo(typeof(DomainEvent)))
        {
            problems.Add(
                $"{integrationEvent.FullName} is a Domain Event; it reaches its own Module's Notification Handlers. Publish a record of the Module's Contracts for other Modules."
            );
        }
        else if (!integrationEvent.IsClass || integrationEvent.IsAbstract || integrationEvent.ContainsGenericParameters)
        {
            problems.Add($"{integrationEvent.FullName} is not a concrete class; publish the concrete Integration Event type.");
        }
        else if (Check(wireName, $"{integrationEvent.FullName}'s wire name", isQueue: false, problems))
        {
            if (_publications.TryGetValue(integrationEvent, out var published))
            {
                if (published != new Publication(wireName, module))
                {
                    problems.Add(
                        $"{integrationEvent.FullName} is published as {published.WireName} by {published.Module?.Name ?? "the application"} and as {wireName} by {module?.Name ?? "the application"}; one Module publishes it, under one wire name, and a new version is a new type under a new wire name."
                    );
                }
                else if (_snapshots.GetValueOrDefault(integrationEvent) != snapshot)
                {
                    problems.Add(
                        $"{integrationEvent.FullName} is published as {Describe(_snapshots.GetValueOrDefault(integrationEvent))} and as {Describe(snapshot)}; a contract is an Integration Event or the Snapshot of one aggregate, with one mapper."
                    );
                }

                ThrowIfAny(problems);
                return;
            }

            if (_received.ContainsKey(integrationEvent))
            {
                problems.Add(Both(integrationEvent, module));
            }
            else if (_typeByWireName.TryGetValue(wireName, out var named))
            {
                problems.Add(
                    $"{integrationEvent.FullName} and {named.FullName} have the wire name {wireName}, ignoring case; publish one under another name: a new version is a new type under a new wire name."
                );
            }
        }

        ThrowIfAny(problems);
        _publications.Add(integrationEvent, new Publication(wireName, module));
        _typeByWireName.Add(wireName, integrationEvent);
        if (snapshot is not null)
        {
            _snapshots.Add(integrationEvent, snapshot);
        }
    }

    /// <summary>
    /// A problem for each message type of the Host that Wolverine would also route where another type is routed: to the
    /// subscriptions of a subscribed type, and to wherever a transport between Hosts sends a published type, such as its
    /// exchange on a broker. Wolverine 6.46.0 routes a type to every message whose wire name, name or full name equals
    /// that type's full name, ignoring case (<c>Subscription.Matches</c>, <c>RoutingScope.Type</c>). A v2 published under
    /// the full name of its v1 would reach every v1 subscription, and every Host that receives v1.
    /// </summary>
    public List<string> RoutingCollisions()
    {
        List<(Type Type, string WireName)> named =
        [
            .. WireNames.Select(wireName => (wireName.Key, wireName.Value)),
            .. MessageTypes
                .Where(type => type.IsAssignableTo(typeof(DomainEvent)))
                .Select(type => (type, MessageNames.WireNameOf(type))),
        ];
        HashSet<Type> subscribed = [.. MessageTypes];
        return
        [
            .. from routed in subscribed.Union(_publications.Keys)
            from message in named
            where message.Type != routed
                && new[] { message.WireName, message.Type.Name, message.Type.FullName }.Contains(
                    routed.FullName,
                    StringComparer.OrdinalIgnoreCase
                )
            select subscribed.Contains(routed)
                ? $"{message.Type.FullName}, under the wire name {message.WireName}, would also reach every subscription of {routed.FullName}: Wolverine routes a subscribed type to every message whose wire name, name or full name equals the type's full name, ignoring case. Give it a wire name and a name of its own."
                : $"{message.Type.FullName}, under the wire name {message.WireName}, would also be sent wherever {routed.FullName} is published to, such as its exchange on a broker: Wolverine routes a published type to every message whose wire name, name or full name equals the type's full name, ignoring case. Give it a wire name and a name of its own.",
        ];
    }

    private static string Describe(SnapshotPublication? snapshot) =>
        snapshot is null
            ? "an Integration Event"
            : $"the Snapshot of {snapshot.Aggregate.FullName} mapped by {snapshot.Mapper.FullName}";

    private static void ThrowIfAny(List<string> problems)
    {
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>
    /// The one rule for a wire name and, with <paramref name="isQueue"/>, for the name of a durable queue, a
    /// subscription's or a delayed command's: adds a problem and returns false when it is missing, invalid or reserved.
    /// </summary>
    public static bool Check(string name, string what, bool isQueue, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{what} is missing; declare one or remove the empty name.");
            return false;
        }

        if (!MessageNames.IsValid(name))
        {
            problems.Add(
                $"{what} {name} is not a valid name: use letters and digits in segments joined by single dots or hyphens, not beginning with amq., which RabbitMQ reserves."
            );
            return false;
        }

        if (isQueue && ReservedQueues.Contains(name))
        {
            problems.Add($"{what} {name} is a queue Wolverine keeps for itself; choose another.");
            return false;
        }

        return true;
    }
}

/// <summary>The wire name an Integration Event is published under, and the Module that publishes it.</summary>
internal sealed record Publication(string WireName, Type? Module);

/// <summary>A Snapshot contract, the aggregate it is the Snapshot of, and the mapper that builds it.</summary>
internal sealed record SnapshotPublication(Type Aggregate, Type Snapshot, Type Mapper);
