using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using Wolverine;
using Wolverine.Util;

namespace SagaSpike.Kit;

/// <summary>One saga a Composition Root declared: its name, which is its queue, its system Actor, its inputs and timeouts.</summary>
public sealed class SagaDeclaration(Type sagaType, string name, string systemActor, Type? module)
{
    /// <summary>The saga's state and handler type.</summary>
    public Type SagaType { get; } = sagaType;

    /// <summary>The saga's durable name: its queue, and the first part of each step's deduplication key.</summary>
    public string Name { get; } = name;

    /// <summary>The system Actor every emission of the saga carries.</summary>
    public string SystemActor { get; } = systemActor;

    /// <summary>The Module whose Composition Root declared the saga, or null for the application's own services.</summary>
    public Type? Module { get; } = module;

    /// <summary>The Integration Events that start or advance the saga.</summary>
    public List<SagaInputDeclaration> Inputs { get; } = [];

    /// <summary>The saga's timeout message types.</summary>
    public List<Type> Timeouts { get; } = [];
}

/// <summary>One Integration Event a saga takes: its type, the Kit's input type for it, and how the Kit wraps it.</summary>
public sealed class SagaInputDeclaration(Type eventType, Type inputType, Func<object, string, string?, Guid, Guid, object> wrap, Func<object, string> key)
{
    /// <summary>The Integration Event's type.</summary>
    public Type EventType { get; } = eventType;

    /// <summary>The closed <see cref="SagaInput{TSaga,TEvent}"/> delivered on the saga's queue.</summary>
    public Type InputType { get; } = inputType;

    /// <summary>
    /// Wraps the event published with <paramref name="options"/>: its identity is the tenant with the business id, read
    /// from the Operation Context headers the Outbox wrote (<see cref="SagaStorage.SagaId"/>), or the business id alone when
    /// <paramref name="tenantInIdentity"/> is false.
    /// </summary>
    public object Wrap(object message, DeliveryOptions? options, bool tenantInIdentity)
    {
        string? Header(string name) => options?.Headers.TryGetValue(name, out var value) is true ? value : null;
        string Required(string name) =>
            Header(name) ?? throw new InvalidOperationException($"{EventType.Name} was published without the {name} header; only the Kit's Outbox reaches a saga.");

        var tenant = Header(OperationContextHeaders.Tenant);
        var business = key(message);
        return wrap(
            message,
            tenantInIdentity ? SagaStorage.SagaId(tenant, business) : business,
            tenant,
            Guid.Parse(Required(OperationContextHeaders.CorrelationId), CultureInfo.InvariantCulture),
            Guid.Parse(Required(IntegrationEventHeaders.DomainEventId), CultureInfo.InvariantCulture)
        );
    }
}

/// <summary>Declares a saga's inputs and timeouts.</summary>
public sealed class SagaBuilder<TSaga>(SagaDeclaration declaration)
    where TSaga : PouryaKitSaga
{
    /// <summary>The saga is started or advanced by <typeparamref name="TEvent"/>, for the business id <paramref name="key"/> returns.</summary>
    public SagaBuilder<TSaga> Input<TEvent>(Func<TEvent, object> key)
        where TEvent : class
    {
        // The wire name is read when the event is published, never here: Wolverine keeps the first name it gives a type,
        // and a Composition Root runs before the Kit declares the Host's wire names.
        declaration.Inputs.Add(
            new SagaInputDeclaration(
                typeof(TEvent),
                typeof(SagaInput<TSaga, TEvent>),
                (message, sagaId, tenant, correlation, eventId) =>
                    new SagaInput<TSaga, TEvent>(sagaId, tenant, correlation, typeof(TEvent).ToMessageTypeName(), eventId, (TEvent)message),
                message => Convert.ToString(key((TEvent)message), CultureInfo.InvariantCulture)!
            )
        );
        return this;
    }

    /// <summary>The saga schedules <typeparamref name="TTimeout"/>, which the Kit routes to the saga's own queue.</summary>
    public SagaBuilder<TSaga> Timeout<TTimeout>()
        where TTimeout : TimeoutMessage
    {
        declaration.Timeouts.Add(typeof(TTimeout));
        return this;
    }
}

/// <summary>Every saga the Host's Composition Roots declared.</summary>
public sealed class SagaDeclarations(IEnumerable<SagaDeclaration> declarations)
{
    private readonly Dictionary<Type, SagaDeclaration> _bySaga = declarations.ToDictionary(declaration => declaration.SagaType);

    /// <summary>The declared sagas.</summary>
    public IReadOnlyCollection<SagaDeclaration> All => _bySaga.Values;

    /// <summary>The declaration of <paramref name="sagaType"/>.</summary>
    public SagaDeclaration Of(Type sagaType) =>
        _bySaga.TryGetValue(sagaType, out var declaration)
            ? declaration
            : throw new InvalidOperationException($"The saga {sagaType.FullName} is declared by no AddSpikeSaga.");

    /// <summary>Whether <paramref name="sagaType"/> was declared.</summary>
    public bool Declares(Type sagaType) => _bySaga.ContainsKey(sagaType);
}

/// <summary>Declares sagas on a Module's builder or the Host's services.</summary>
public static class SagaServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares the saga <typeparamref name="TSaga"/> under <paramref name="name"/>, its queue, whose emissions carry the
        /// system Actor <paramref name="systemActor"/>.
        /// </summary>
        public IServiceCollection AddSpikeSaga<TSaga>(string name, string systemActor, Action<SagaBuilder<TSaga>> inputs)
            where TSaga : PouryaKitSaga
        {
            var declaration = new SagaDeclaration(typeof(TSaga), name, systemActor, (services as IModuleServices)?.Module);
            inputs(new SagaBuilder<TSaga>(declaration));
            services.AddSingleton(declaration);
            services.TryAddSingleton<SagaDeclarations>();
            services.TryAddSingleton<SagaSpikeOptions>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IWolverineExtension, SagaWolverineExtension>());
            return services;
        }
    }
}
