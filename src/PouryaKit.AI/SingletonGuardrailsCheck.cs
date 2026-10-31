using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.AI;

// The chat client, a singleton, resolves its guardrails once, when it is built, so a scoped guardrail would fail only at
// the first run and a transient one would live as long as the client: one problem naming each that is not a singleton.
internal sealed class SingletonGuardrailsCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services) =>
        composition
            .Registrations.Where(descriptor =>
                !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IChatGuardrail)
                && descriptor.Lifetime != ServiceLifetime.Singleton
            )
            .Select(descriptor =>
                $"The Host registers the IChatGuardrail {descriptor.ImplementationType?.FullName ?? $"the factory {descriptor.ImplementationFactory!.Method.DeclaringType?.FullName}.{descriptor.ImplementationFactory.Method.Name}"} as {descriptor.Lifetime}, and the chat client resolves its guardrails once, when it is built: register each IChatGuardrail as a singleton."
            );
}
