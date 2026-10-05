using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests;

// Not part of the test Module: this assembly holds the two wiring mistakes the registration must reject.
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

public sealed class RegistrationTests
{
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
