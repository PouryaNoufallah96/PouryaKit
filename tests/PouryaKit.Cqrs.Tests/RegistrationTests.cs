using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs.Tests.Module;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests;

// Not part of the test Module: this assembly holds the wiring mistakes the registration must reject.
internal sealed record OrphanCommand : Command;

internal sealed record TwiceHandledQuery : Query<string>;

internal sealed class FirstTwiceHandledHandler : QueryHandler<TwiceHandledQuery, string>
{
    public override Task<Result<string>> Handle(TwiceHandledQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>("first");
}

internal sealed class SecondTwiceHandledHandler : QueryHandler<TwiceHandledQuery, string>
{
    public override Task<Result<string>> Handle(TwiceHandledQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>("second");
}

internal interface IAuditedOperation;

// Registers under IAuthorizer<T> for a T the dispatcher never resolves, so it would never run.
internal sealed class MarkerAuthorizer : IAuthorizer<IAuditedOperation>
{
    public Task<Result> AuthorizeAsync(IAuditedOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

// Generic definitions: each would otherwise be skipped silently or registered as an open implementation.
internal sealed record EchoQuery<TValue>(TValue Value) : Query<TValue>
    where TValue : notnull;

internal sealed class EchoHandler<TValue> : QueryHandler<EchoQuery<TValue>, TValue>
    where TValue : notnull
{
    public override Task<Result<TValue>> Handle(EchoQuery<TValue> query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<TValue>>(query.Value);
}

internal sealed class AnyOperationAuthorizer<TOperation> : IAuthorizer<TOperation>
    where TOperation : notnull
{
    public Task<Result> AuthorizeAsync(TOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class GenericClosedAuthorizer<TUnused> : IAuthorizer<TwiceHandledQuery>
{
    public Task<Result> AuthorizeAsync(TwiceHandledQuery operation, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal abstract record ThingDomainEvent : DomainEvent;

internal sealed record ThingNamedDomainEvent : ThingDomainEvent;

internal sealed record ThingMovedDomainEvent : ThingDomainEvent;

internal sealed class TwoEventsHandler
    : IDomainEventHandler<ThingNamedDomainEvent>,
        IDomainEventHandler<ThingMovedDomainEvent>
{
    public Task<Result> Handle(ThingNamedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> Handle(ThingMovedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

// The drain runs the handlers of a Domain Event's exact type, so this one would never run.
internal sealed class AnyThingEventHandler : IDomainEventHandler<ThingDomainEvent>
{
    public Task<Result> Handle(ThingDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class AnyEventHandler<TDomainEvent> : IDomainEventHandler<TDomainEvent>
    where TDomainEvent : DomainEvent
{
    public Task<Result> Handle(TDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

public sealed class RegistrationTests
{
    [Fact]
    public void A_tenancy_declaration_on_the_hosts_services_that_names_no_assembly_fails()
    {
        // No Module's assembly can be inferred there, so it would make no command or query need a tenant.
        var thrown = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddPouryaKitTenancy());

        Assert.Equal("featureAssemblies", thrown.ParamName);
    }

    [Fact]
    public void An_authorizer_for_a_marker_interface_fails_registration()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPouryaKitCqrs(typeof(RegistrationTests).Assembly)
        );

        Assert.Contains($"{typeof(MarkerAuthorizer).FullName} is an authorizer, but not for one concrete", error.Message);
    }

    [Fact]
    public void A_generic_query_handler_or_authorizer_fails_registration()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPouryaKitCqrs(typeof(RegistrationTests).Assembly)
        );

        Type[] generics =
        [
            typeof(EchoQuery<>),
            typeof(EchoHandler<>),
            typeof(AnyOperationAuthorizer<>),
            typeof(GenericClosedAuthorizer<>),
            typeof(AnyEventHandler<>),
        ];
        Assert.All(generics, type => Assert.Contains($"{type.FullName} is generic", error.Message));
    }

    [Fact]
    public void A_domain_event_handler_for_several_events_or_for_an_abstract_one_fails_registration()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPouryaKitCqrs(typeof(RegistrationTests).Assembly)
        );

        Assert.Contains($"{typeof(TwoEventsHandler).FullName} handles 2 Domain Event types", error.Message);
        Assert.Contains($"{typeof(AnyThingEventHandler).FullName} handles ThingDomainEvent, which is abstract", error.Message);
    }

    [Fact]
    public void A_command_with_no_handler_and_a_query_with_two_fail_registration_before_the_container_is_built()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPouryaKitCqrs(typeof(RegistrationTests).Assembly)
        );

        Assert.Contains($"{typeof(OrphanCommand).FullName} has 0 handlers", error.Message);
        Assert.Contains($"{typeof(TwiceHandledQuery).FullName} has 2 handlers", error.Message);
    }

    [Fact]
    public void A_keyed_service_of_a_handlers_type_is_no_handler_and_the_scanned_handler_is_registered_beside_it()
    {
        var module = typeof(ThingsGateway).Assembly;
        var handler = new ServiceCollection()
            .AddPouryaKitCqrs(module)
            .First(descriptor => descriptor.ServiceType.IsGenericType && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(CommandHandler<>));
        var services = new ServiceCollection().AddKeyedScoped(handler.ServiceType, "audit", handler.ImplementationType!);

        services.AddPouryaKitCqrs(module);

        var registered = services.Where(descriptor => descriptor.ServiceType == handler.ServiceType).ToList();
        Assert.Equal("audit", Assert.Single(registered, descriptor => descriptor.IsKeyedService).ServiceKey);
        Assert.Equal(handler.ImplementationType, Assert.Single(registered, descriptor => !descriptor.IsKeyedService).ImplementationType);
    }
}
