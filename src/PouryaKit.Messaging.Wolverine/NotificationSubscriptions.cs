using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Every Notification Handler subscription of one Host. It lives in the Host's service collection, not in a static, so
/// every Module's declaration adds to the same one, whenever it runs, and two Hosts in one process never share it.
/// Names are compared ignoring case, because local queue names are lower-cased.
/// </summary>
internal sealed class NotificationSubscriptions
{
    private readonly Dictionary<string, NotificationSubscription> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Type> _domainEventByWireName = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<NotificationSubscription> All => _byName.Values;

    public IEnumerable<Type> DomainEvents => _domainEventByWireName.Values;

    public static NotificationSubscriptions In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(NotificationSubscriptions)) is { } existing)
        {
            return (NotificationSubscriptions)existing.ImplementationInstance!;
        }

        var subscriptions = new NotificationSubscriptions();
        services.AddSingleton(subscriptions);
        return subscriptions;
    }

    public bool HasAny(Type domainEvent) => _domainEventByWireName.ContainsValue(domainEvent);

    public NotificationSubscription Named(string name) =>
        _byName.TryGetValue(name, out var subscription) ? subscription : throw new UnknownSubscriptionException(name);

    /// <summary>
    /// Adds every subscription, or none: throws, naming each problem, when a name is missing or invalid, or equal
    /// ignoring case to one another Notification Handler or Domain Event already has. Declaring the same handler again
    /// adds nothing.
    /// </summary>
    public void Add(IReadOnlyList<NotificationSubscription> subscriptions, List<string> problems)
    {
        Dictionary<string, NotificationSubscription> byName = new(_byName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Type> domainEventByWireName = new(_domainEventByWireName, StringComparer.OrdinalIgnoreCase);
        foreach (var subscription in subscriptions)
        {
            var wireName = MessageNames.WireNameOf(subscription.DomainEvent);
            if (Check(wireName, $"{subscription.DomainEvent.FullName}'s wire name", problems))
            {
                if (!domainEventByWireName.TryAdd(wireName, subscription.DomainEvent)
                    && domainEventByWireName[wireName] != subscription.DomainEvent)
                {
                    problems.Add(
                        $"{subscription.DomainEvent.FullName} and {domainEventByWireName[wireName].FullName} have the wire name {wireName}, ignoring case; give one a WireName of its own."
                    );
                }
            }

            if (!Check(subscription.Name, $"{subscription.Handler.FullName}'s subscription name", problems))
            {
                continue;
            }

            if (byName.TryGetValue(subscription.Name, out var existing))
            {
                if (existing.Handler != subscription.Handler || existing.DomainEvent != subscription.DomainEvent)
                {
                    problems.Add(
                        $"{subscription.Handler.FullName} and {existing.Handler.FullName} have the subscription name {subscription.Name}, ignoring case; give one a SubscriptionName of its own."
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

        foreach (var (wireName, domainEvent) in domainEventByWireName)
        {
            _domainEventByWireName.TryAdd(wireName, domainEvent);
        }
    }

    private static bool Check(string name, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{what} is missing; declare one or remove the empty name.");
            return false;
        }

        if (!MessageNames.IsValid(name))
        {
            problems.Add(
                $"{what} {name} is not valid as a queue name: use letters and digits in segments joined by single dots or hyphens."
            );
            return false;
        }

        return true;
    }
}
