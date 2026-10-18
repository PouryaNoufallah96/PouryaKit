using Wolverine;

namespace SagaSpike.Kit;

// Applied when Wolverine's options are first resolved, after every Composition Root, as the Kit's own extension is. The
// Kit turns Wolverine's discovery off, so each declared saga is included by type: a public type, Wolverine's generated
// chain (the exception to ADR 0001 and ADR 0006).
internal sealed class SagaWolverineExtension(SagaDeclarations sagas, SagaSpikeOptions options) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        foreach (var saga in sagas.All)
        {
            wolverine.Discovery.IncludeType(saga.SagaType);
            // Each input type is the saga's alone, and goes to the saga's own durable queue, as the timeouts do.
            foreach (var input in saga.Inputs)
            {
                wolverine.PublishMessage(input.InputType).ToLocalQueue(saga.Name);
            }

            foreach (var timeout in saga.Timeouts)
            {
                wolverine.PublishMessage(timeout).ToLocalQueue(saga.Name);
            }
        }

        if (options.KitRoute)
        {
            wolverine.PublishWithMessageRoutingSource(new SagaRouteSource(sagas, options));
        }

        if (options.KitStorage)
        {
            wolverine.Policies.Add(new SagaStepPolicy(sagas));
        }
    }
}
