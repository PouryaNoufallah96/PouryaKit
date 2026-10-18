using JasperFx.CodeGeneration.Model;
using PouryaKit.Cqrs;
using SagaSpike.Contracts;
using SagaSpike.Host;
using SagaSpike.Kit;
using SagaSpike.Shipping;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaSpike.Tests;

// Item 8, tried and recorded: a Wolverine middleware begins an Operation around a step, on the step's own service scope,
// with the Operation Context the event's headers carry. The step then reads that context; it still runs in no Unit of
// Work, and the Kit's Integration Event publisher refuses it. The probe saga starts from the same event as the fulfilment
// saga, so one event reaches two sagas and Billing's subscription.
public sealed class OperationContextTests(Postgres postgres) : SpikeTest(postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Middleware_can_begin_an_operation_around_a_step_which_still_runs_outside_the_kits_unit_of_work_and_publisher(bool middleware)
    {
        var host = await StartAsync(
            new SpikeHostOptions
            {
                Services = services => services.AddSpikeSaga<ContextProbeSaga>("shipping.context-probe", "shipping.context-probe", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
                Wolverine = wolverine =>
                {
                    // Wolverine's default, NotAllowed, refuses the step's chain when it is generated: the Kit's Operation is
                    // an opaque scoped factory and its publisher an internal type, both reached only by service location.
                    wolverine.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                    if (middleware)
                    {
                        wolverine.Policies.AddMiddleware(typeof(RestoreOperationContext), chain => chain is SagaChain { SagaType: var saga } && saga == typeof(ContextProbeSaga));
                    }
                },
            }
        );
        var order = Guid.CreateVersion7();
        var placing = Context("tenant-a");

        await PlaceAsync(host, placing, order);

        await WaitUntilAsync("select count(*) = 1 from wolverine.contextprobesaga_saga where id = $1", $"tenant-a:{order}");
        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where id = $1", $"tenant-a:{order}");
        await WaitUntilAsync("select count(*) = 1 from billing.pouryakit_receipts where subscription = 'billing.order-placed'");
        var seen = ContextProbe.Seen[order];
        if (middleware)
        {
            Assert.Equal(
                (ActorKind.System, "shipping.context-probe", "tenant-a", placing.CorrelationId, typeof(ShippingModule)),
                (seen.Context!.Actor.Kind, seen.Context.Actor.Id, seen.Context.Tenant, seen.Context.CorrelationId, seen.Module)
            );
        }
        else
        {
            Assert.Null(seen.Context);
        }

        Assert.False(seen.UnitOfWorkActive);
        Assert.Contains("outside a Notification Handler's delivery", seen.PublishRefused);
    }

    // Under Wolverine's own default the step cannot even take them: its chain is refused when Wolverine generates it, at
    // the first message, and the delivery is set aside.
    [Fact]
    public async Task Under_wolverines_default_service_location_policy_a_step_cannot_take_the_kits_operation()
    {
        var host = await StartAsync(
            new SpikeHostOptions
            {
                Services = services => services.AddSpikeSaga<ContextProbeSaga>("shipping.context-probe", "shipping.context-probe", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
            }
        );

        await PlaceAsync(host, Context("tenant-a"), Guid.CreateVersion7());

        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_dead_letters where received_at = 'local://shipping.context-probe/'");
        Assert.Equal(
            "Wolverine.Configuration.InvalidServiceLocationException",
            await Db.ScalarAsync<string>(ConnectionString, "select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://shipping.context-probe/'")
        );
    }
}
