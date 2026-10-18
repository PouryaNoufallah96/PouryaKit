using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// What the Host's sagas need of its final registrations, read when it is built: a saga Wolverine can generate the steps
// of, inputs the Host has wire names for, timeouts that keep the identity they were scheduled under, a queue of its own,
// and Wolverine's service location policy at its default, so no step reaches a Kit scoped service such as the Operation
// (ADR 0013). The policy is a value of Wolverine's options, whose extensions resolving them applies, naming every type
// Wolverine routes: the routing check registered before this one has declared the Host's wire names by then, and this
// one reads the options only once they are all declared and its own declarations are sound, so a Host whose routes fail
// fixes no name for the process.
internal sealed class SagaCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var sagas = services.GetRequiredService<SagaDeclarations>().All.ToList();
        var subscriptions = services.GetRequiredService<Subscriptions>();
        var wireNames = subscriptions.WireNames;
        var delayedCommands = services.GetService<DelayedCommandDeclarations>()?.All ?? [];
        List<string> problems = [];
        foreach (var saga in sagas)
        {
            var type = saga.SagaType;
            if (!type.IsVisible || type.IsAbstract || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) is null)
            {
                problems.Add(
                    $"The Saga {saga.Name}, {type.FullName}, is not a public concrete class with a public parameterless constructor: Wolverine generates the code of its steps, which names and creates it. Make it public."
                );
            }

            foreach (var input in saga.Inputs)
            {
                if (!input.EventType.IsVisible)
                {
                    problems.Add(
                        $"The Saga {saga.Name} takes {input.EventType.FullName}, which is not public: the code Wolverine generates for the step names it. Make it public, as a Module's Contracts are."
                    );
                }

                if (!wireNames.ContainsKey(input.EventType))
                {
                    problems.Add(
                        $"The Saga {saga.Name} takes {input.EventType.FullName}, which no Module of this Host publishes and no Receive declares, so it has no wire name to claim its steps under. Declare Publish<{input.EventType.Name}>(wireName) in the publishing Module's Composition Root, or Receive<{input.EventType.Name}>(wireName) in this Host's composition."
                    );
                }
            }

            foreach (var timeout in saga.Timeouts)
            {
                if (!timeout.IsVisible || timeout.IsAbstract)
                {
                    problems.Add(
                        $"The timeout {timeout.FullName} of the Saga {saga.Name} is not a public concrete record: the code Wolverine generates for the step names it. Make it public."
                    );
                }

                if (SagaChain.DetermineSagaIdMember(timeout, type) is { } member)
                {
                    problems.Add(
                        $"The timeout {timeout.FullName} of the Saga {saga.Name} has the member {member.Name}, which Wolverine reads as the identity of the saga it reaches, in place of the one it was scheduled under. Rename it."
                    );
                }
            }

            if (subscriptions.All.Any(subscription => subscription.Name == saga.Name) || delayedCommands.Any(delayed => delayed.Name == saga.Name))
            {
                problems.Add(
                    $"The Saga {type.FullName} and a subscription or a delayed command have the name {saga.Name}, ignoring case; each needs a queue of its own."
                );
            }
        }

        // Resolving the options includes each saga in Wolverine's discovery, which throws for one it cannot generate.
        if (
            sagas.Count > 0
            && problems.Count == 0
            && WireNaming.AllDeclared(wireNames)
            && services.GetService<WolverineOptions>()?.ServiceLocationPolicy is { } policy and not ServiceLocationPolicy.NotAllowed
        )
        {
            problems.Add(
                $"The Host runs the Sagas {string.Join(", ", sagas.Select(saga => saga.Name))}, but Wolverine's ServiceLocationPolicy is {policy}, so a Saga's step could reach the Kit's scoped services, the Operation and its publisher among them, outside every Operation. Remove the configuration that sets it: a Host with a Saga keeps Wolverine's default, NotAllowed."
            );
        }

        return problems;
    }
}
