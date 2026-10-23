using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// One saga a Composition Root declared: its name, which is its queue, the first part of each claim's key and of each
// input's message type name, and its state table's name, its system Actor, the Module that declared it, its inputs and
// its timeouts.
internal sealed class SagaDeclaration(Type sagaType, string name, string systemActor, Type? module)
{
    public Type SagaType { get; } = sagaType;

    public string Name { get; } = name;

    // Wolverine names a saga's table after the type's simple name and keys it by the saga's identity alone, so two sagas
    // of one simple name would share their states: the Kit names it after the saga's name, which outlives a rename of the
    // type. Dots and hyphens become underscores, so two names can still map to one table; SagaCheck refuses that.
    public string Table { get; } = $"{name.Replace('.', '_').Replace('-', '_')}_saga";

    public string SystemActor { get; } = systemActor;

    public Type? Module { get; } = module;

    public List<SagaInputDeclaration> Inputs { get; } = [];

    public List<Type> Timeouts { get; } = [];
}

// One Integration Event a saga takes, the Kit's input type for it, how the event names its business id, and how the Kit
// builds the input from the event, its saga identity, tenant, correlation and identity.
internal sealed class SagaInputDeclaration(
    Type eventType,
    Type inputType,
    Func<object, string> key,
    Func<object, string, string?, Guid, Guid, object> input
)
{
    public Type EventType { get; } = eventType;

    public Type InputType { get; } = inputType;

    // The event as the saga's input: its saga identity is the tenant with the business id, its tenant, correlation and
    // identity those the Outbox wrote in its headers, read alike on the Kit's route and on a broker's fan-out.
    public object Wrap(object integrationEvent, IReadOnlyDictionary<string, string?> headers)
    {
        string? Header(string name) => headers.TryGetValue(name, out var value) ? value : null;
        string Required(string name) =>
            Header(name)
            ?? throw new InvalidOperationException(
                $"{EventType.FullName} was published without the {name} header, so it names no saga. Only the Unit of Work's Outbox publishes an Integration Event a Saga takes."
            );

        var tenant = Header(OperationContextHeaders.Tenant);
        return input(
            integrationEvent,
            SagaStorage.SagaId(tenant, key(integrationEvent)),
            tenant,
            Guid.Parse(Required(OperationContextHeaders.CorrelationId), CultureInfo.InvariantCulture),
            Guid.Parse(Required(IntegrationEventHeaders.DomainEventId), CultureInfo.InvariantCulture)
        );
    }
}

// Every saga one Host declares. It lives in the Host's service collection, as the subscriptions do, so every Module's
// declaration adds to the same one and two Hosts in one process never share it.
internal sealed class SagaDeclarations
{
    private readonly Dictionary<Type, SagaDeclaration> _bySaga = [];

    public IEnumerable<SagaDeclaration> All => _bySaga.Values;

    public static SagaDeclarations In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(SagaDeclarations)) is { } existing)
        {
            return (SagaDeclarations)existing.ImplementationInstance!;
        }

        var declarations = new SagaDeclarations();
        services.AddSingleton(declarations);
        return declarations;
    }

    public SagaDeclaration Of(Type sagaType) =>
        _bySaga.TryGetValue(sagaType, out var declaration)
            ? declaration
            : throw new InvalidOperationException($"The Saga {sagaType.FullName} has no AddPouryaKitSaga declaration in this Host.");

    public bool Declares(Type sagaType) => _bySaga.ContainsKey(sagaType);

    // Adds the declaration, or throws naming each problem: its name is missing, invalid, a queue Wolverine keeps for
    // itself or another saga's, the saga is declared again, or it takes no event or one event twice.
    public void Add(SagaDeclaration declaration)
    {
        List<string> problems = [];
        Subscriptions.Check(declaration.Name, $"The Saga {declaration.SagaType.FullName}'s name", isQueue: true, problems);
        if (_bySaga.ContainsKey(declaration.SagaType))
        {
            problems.Add($"The Saga {declaration.SagaType.FullName} is declared twice; declare it once.");
        }
        else if (_bySaga.Values.FirstOrDefault(other => other.Name == declaration.Name) is { } named)
        {
            problems.Add(
                $"The Sagas {declaration.SagaType.FullName} and {named.SagaType.FullName} have the name {declaration.Name}, ignoring case; each needs a queue of its own."
            );
        }

        if (declaration.Inputs.Count == 0)
        {
            problems.Add($"The Saga {declaration.SagaType.FullName} takes no Integration Event, so nothing starts it. Declare each with Input<TEvent>(key).");
        }

        problems.AddRange(
            declaration
                .Inputs.GroupBy(input => input.EventType)
                .Where(inputs => inputs.Count() > 1)
                .Select(inputs => $"The Saga {declaration.SagaType.FullName} takes {inputs.Key.FullName} twice; declare each input once.")
        );
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        _bySaga.Add(declaration.SagaType, declaration);
    }
}
