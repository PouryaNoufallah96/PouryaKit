using Wolverine;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// Applied when Wolverine's options are first resolved, after every Composition Root, as the Kit's own extension is. The
// Kit turns Wolverine's discovery off, so each declared saga is included by type: a public type, whose chains Wolverine
// generates, the exception to ADR 0001 and ADR 0006 (ADR 0013). Each input type is its saga's alone and goes to the
// saga's own durable queue, as its timeouts do.
internal sealed class SagaWolverineExtension(SagaDeclarations sagas) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        foreach (var saga in sagas.All)
        {
            wolverine.Discovery.IncludeType(saga.SagaType);
            foreach (var input in saga.Inputs)
            {
                wolverine.PublishMessage(input.InputType).ToLocalQueue(saga.Name);
            }

            foreach (var timeout in saga.Timeouts)
            {
                wolverine.PublishMessage(timeout).ToLocalQueue(saga.Name);
            }
        }

        wolverine.PublishWithMessageRoutingSource(new SagaRouteSource(sagas));
        wolverine.Policies.Add(new SagaStepPolicy(sagas));
    }
}
