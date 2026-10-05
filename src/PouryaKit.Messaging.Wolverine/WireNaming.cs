using PouryaKit.Domain;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

// Wolverine's naming strategies are process-wide, so this one is a pure function of the type: two Hosts in one process
// can never disagree on a wire name.
internal sealed class WireNaming : IMessageTypeNaming
{
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        messageTypeName =
            messageType.IsAssignableTo(typeof(DomainEvent)) && !messageType.IsAbstract
                ? MessageNames.WireNameOf(messageType)
                : "";
        return MessageNames.IsValid(messageTypeName);
    }
}
