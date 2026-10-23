using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using Wolverine;
using Wolverine.RDBMS.Sagas;

namespace PouryaKit.Messaging.Wolverine.Sagas;

/// <summary>Declares a Module's Sagas in its Composition Root, part of the Saga Capability.</summary>
public static class SagaServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares that the Module whose Composition Root calls it on its builder (<see cref="IModuleServices"/>) runs the
        /// Saga <typeparamref name="TSaga"/> under <paramref name="name"/>: its durable queue, the first part of each step's
        /// claim and of each input's stored message type (<c>{name}.{event wire name}</c>), and its state table's name,
        /// <c>{name}_saga</c> with dots and hyphens as underscores, so it is stable; keep it when the type is renamed. Each
        /// step runs outside every Operation, with no Operation Context, on Wolverine's own chain, which the Kit opens on a
        /// transaction of the message store's database: the step's claim, keyed by the saga's name, the input's wire name
        /// and the event's identity, its state in that table of the message store's schema, and what it emits and schedules
        /// commit in it together,
        /// so a second delivery of one event, or a redelivery after a crash between the commit and the mark of its
        /// envelope, applies nothing (ADR 0013). The saga's identity is the event's tenant with its business id; an event
        /// with no tenant, or one whose identity names no saga it does not start, is refused before any step, retried,
        /// then set aside as a dead letter with <see cref="SagaInputRefusedException"/>. What a step emits carries the
        /// system Actor named <paramref name="systemActor"/>. <paramref name="declare"/> names the Integration Events the
        /// saga takes and the timeouts it schedules. Throws here, while the Host is composed, when the name is missing,
        /// invalid, a queue Wolverine keeps for itself or another saga's, when the saga is declared twice, and when it
        /// takes no event or one event twice. The Host then fails when it is built if the saga type is not a public
        /// concrete class, if the declaration belongs to no Module in a Host of Modules or to one the Host does not compose,
        /// if an event it takes is a Snapshot, is not public or has no wire name in the Host, if a timeout names an identity
        /// member of its own, if a subscription or delayed command has its name, if its name, and so its state table's,
        /// does not start with a letter, if its state table's name is longer than PostgreSQL's 63 characters, if another
        /// saga's name gives the same state table or input name, if Wolverine's service location policy is not
        /// <c>NotAllowed</c>, or if Wolverine would keep the saga's state with another saga storage than its lightweight
        /// one in the message store of <c>UsePouryaKitWolverine(connectionName)</c> of
        /// <c>PouryaKit.Messaging.Wolverine.EFCore</c>, as with a Marten store (<c>UsePouryaKitMarten</c>), alone or beside
        /// it; it fails to start when the saga's queue is not durable. The saga's claims table and state table
        /// are created with the message store when the Host starts; the claims are kept after the saga completes. So
        /// a saga and a Stream (Marten) cannot share one Host yet; making the Kit's sagas keep the lightweight provider beside Marten's is a later package.
        /// </summary>
        public IServiceCollection AddPouryaKitSaga<TSaga>(string name, string systemActor, Action<SagaBuilder<TSaga>> declare)
            where TSaga : PouryaKitSaga
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(systemActor);
            ArgumentNullException.ThrowIfNull(declare);

            var declaration = new SagaDeclaration(typeof(TSaga), (name ?? "").ToLowerInvariant(), systemActor, IModuleServices.ModuleOf(services));
            declare(new SagaBuilder<TSaga>(declaration));
            SagaDeclarations.In(services).Add(declaration);
            var subscriptions = Subscriptions.In(services);
            foreach (var input in declaration.Inputs)
            {
                subscriptions.AddSagaInput(new SagaInputRoute(input.EventType, declaration.Name, input.Wrap));
            }

            // Wolverine creates the state table of each saga it knows with the message store, not at the saga's first step.
            services.AddSingleton(new SagaTableDefinition(typeof(TSaga), declaration.Table));
            DelayedCommandDeclarations.In(services);
            DurableQueuesCheck.AddTo(services);
            // The routing check declares the Host's wire names; the saga check after it resolves Wolverine's options, which
            // names every type Wolverine routes, so it must never come first.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, RoutingCheck>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, SagaCheck>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IWolverineExtension, SagaWolverineExtension>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SagaStepTable>());
            return services;
        }
    }
}
