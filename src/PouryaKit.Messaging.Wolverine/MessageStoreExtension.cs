using Microsoft.Extensions.Options;
using Wolverine;

namespace PouryaKit.Messaging.Wolverine;

// Applied when Wolverine's options are first resolved, so a Host or a test can still change the settings after
// UsePouryaKitWolverine.
internal sealed class MessageStoreExtension(IOptions<PouryaKitMessageStoreOptions> options)
    : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        wolverine.Durability.Mode = options.Value.Durability;
        wolverine.Durability.InboxStaleTime = options.Value.InboxStaleTime;
    }
}
