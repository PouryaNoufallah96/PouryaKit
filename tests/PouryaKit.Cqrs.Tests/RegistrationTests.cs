using Microsoft.Extensions.DependencyInjection;
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

public sealed class RegistrationTests
{
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
        ];
        Assert.All(generics, type => Assert.Contains($"{type.FullName} is generic", error.Message));
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
}
