using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PouryaKit.Messaging;

/// <summary>
/// The durable names a delivery is stored and routed under: a message's wire name and a Notification Handler's
/// subscription name. Each is derived from the type by convention unless the type declares a stable one with
/// <see cref="WireNameAttribute"/> or <see cref="SubscriptionNameAttribute"/>. A derived name changes when its type is
/// renamed, which strands the deliveries stored under the old name; declare the old name to keep them.
/// </summary>
public static partial class MessageNames
{
    /// <summary>
    /// The wire name of <paramref name="messageType"/>: its <see cref="WireNameAttribute"/>, or its assembly's name and
    /// its own name without a <c>DomainEvent</c> suffix, each in kebab case, as in <c>catalog.product-created</c> for
    /// <c>ProductCreatedDomainEvent</c> in the assembly <c>Catalog</c>.
    /// </summary>
    public static string WireNameOf(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (messageType.GetCustomAttribute<WireNameAttribute>() is { } declared)
        {
            return declared.Name;
        }

        var assembly = string.Join('.', (messageType.Assembly.GetName().Name ?? "").Split('.').Select(Kebab));
        return $"{assembly}.{Kebab(WithoutSuffix(messageType.Name, "DomainEvent"))}";
    }

    /// <summary>
    /// The subscription name of the Notification Handler <paramref name="handlerType"/> of
    /// <paramref name="messageType"/>: its <see cref="SubscriptionNameAttribute"/>, or the message's wire name followed
    /// by the handler's own name without a <c>NotificationHandler</c> or <c>Handler</c> suffix, in kebab case, as in
    /// <c>catalog.product-created.announce-new-product</c> for <c>AnnounceNewProductHandler</c>.
    /// </summary>
    public static string SubscriptionNameOf(Type handlerType, Type messageType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(messageType);
        if (handlerType.GetCustomAttribute<SubscriptionNameAttribute>() is { } declared)
        {
            return declared.Name;
        }

        var handler = WithoutSuffix(WithoutSuffix(handlerType.Name, "NotificationHandler"), "Handler");
        return $"{WireNameOf(messageType)}.{Kebab(handler)}";
    }

    /// <summary>
    /// True when <paramref name="name"/> can serve as a wire name or a subscription name, which is also a queue name:
    /// letters and digits in segments joined by single dots or hyphens. Names are compared ignoring case, because
    /// transports such as local queues lower-case them.
    /// </summary>
    public static bool IsValid(string? name) => name is not null && ValidName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*$")]
    private static partial Regex ValidName();

    private static string WithoutSuffix(string name, string suffix) =>
        name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;

    // ProductCreated -> product-created, HTTPRequestSent -> http-request-sent, Snake_Case -> snake-case.
    private static string Kebab(string name)
    {
        var kebab = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                kebab.Append('-');
                continue;
            }

            if (
                char.IsUpper(c)
                && i > 0
                && name[i - 1] != '_'
                && (
                    char.IsLower(name[i - 1])
                    || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))
                )
            )
            {
                kebab.Append('-');
            }

            kebab.Append(char.ToLowerInvariant(c));
        }

        return kebab.ToString();
    }
}
