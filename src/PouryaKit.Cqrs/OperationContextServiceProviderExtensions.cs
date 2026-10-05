using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

public static class OperationContextServiceProviderExtensions
{
    extension(IServiceProvider scope)
    {
        /// <summary>
        /// Sets the Operation Context of the scope this provider belongs to, once, before the Operation runs. Whoever
        /// opens an Operation's scope calls it: a Module's Gateway, the HTTP boundary, or a message delivery.
        /// </summary>
        public void SetOperationContext(OperationContext context)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(context);
            var slot = scope.GetRequiredService<OperationContextSlot>();
            if (slot.Context is not null)
            {
                throw new InvalidOperationException(
                    "This scope already has an Operation Context. A scope runs one Operation; open a new scope for another."
                );
            }

            slot.Context = context;
        }
    }
}

/// <summary>Holds the Operation Context of one scope; <see cref="OperationContext"/> is resolved from it.</summary>
internal sealed class OperationContextSlot
{
    public OperationContext? Context { get; set; }

    // An Operation with no context never runs as an anonymous or system Actor by default: reading it fails.
    public static OperationContext Read(IServiceProvider services) =>
        services.GetRequiredService<OperationContextSlot>().Context
        ?? throw new InvalidOperationException(
            "No Operation Context was set for this scope. Send the Operation through a Module's Gateway with an OperationContext, or call SetOperationContext on the scope before it runs."
        );
}
