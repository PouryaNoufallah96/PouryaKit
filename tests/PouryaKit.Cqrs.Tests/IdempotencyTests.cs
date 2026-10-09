using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs.Tests.Module;

namespace PouryaKit.Cqrs.Tests;

// The dispatcher's side of request idempotency, with a stand-in store; the store's own guarantees are proven against
// PostgreSQL in ModularMonolith.Tests.
public sealed class IdempotencyTests
{
    // The command as its key names it: its full name, then its assembly's simple name.
    private const string SealThing = "PouryaKit.Cqrs.Tests.Module.SealThingCommand, PouryaKit.Cqrs.Tests.Module";

    [Fact]
    public async Task Only_an_outermost_command_with_a_request_id_reserves_its_key_inside_the_unit_of_work_before_its_handler()
    {
        await using var host = TestHost.Build(withIdempotencyStore: true);
        using var withRequestId = Begun(host, "r1");
        using var withoutRequestId = Begun(host, requestId: null);

        var sealedOnce = await ThingsGateway.SealThing(Dispatcher(withRequestId));
        var sealedPlainly = await ThingsGateway.SealThing(Dispatcher(withoutRequestId));

        Assert.Equal("sealed", sealedOnce.Value);
        Assert.Equal("sealed", sealedPlainly.Value);
        // The nested SealPartCommand opts in too, and joins the Unit of Work without a key of its own.
        Assert.Equal(
            [
                "log:Running SealThingCommand",
                "validate SealThingCommand",
                "uow:begin",
                $"reserve {SealThing} r1",
                "handle SealThingCommand",
                "log:Running SealPartCommand",
                "validate SealPartCommand",
                "handle SealPartCommand",
                "log:Succeeded SealPartCommand",
                $"record {SealThing} \"sealed\"",
                "uow:commit",
                "log:Succeeded SealThingCommand",
            ],
            Trace(withRequestId)
        );
        Assert.DoesNotContain(Trace(withoutRequestId), step => step.StartsWith("reserve", StringComparison.Ordinal));
        Assert.DoesNotContain(Trace(withoutRequestId), step => step.StartsWith("record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_id_in_a_host_without_an_idempotency_store_fails_the_dispatch_before_the_handler_runs()
    {
        await using var host = TestHost.Build();
        using var scope = Begun(host, "r1");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.SealThing(Dispatcher(scope)));

        Assert.Contains("no IIdempotencyStore is registered", thrown.Message);
        Assert.DoesNotContain("uow:begin", Trace(scope));
        Assert.DoesNotContain("handle SealThingCommand", Trace(scope));
    }

    [Fact]
    public async Task A_request_id_reaching_a_command_that_does_not_opt_in_fails_the_dispatch_before_the_handler_runs()
    {
        await using var host = TestHost.Build(withIdempotencyStore: true);
        using var scope = Begun(host, "r1");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.Ping(Dispatcher(scope)));

        Assert.Contains("does not opt in with [Idempotent]", thrown.Message);
        Assert.DoesNotContain("handle PingCommand", Trace(scope));
        Assert.DoesNotContain(Trace(scope), step => step.StartsWith("reserve", StringComparison.Ordinal));
    }

    // The key stores an Operation without a tenant as the empty string, so no tenant may be blank.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_tenant_is_refused_so_no_tenant_shares_the_key_of_an_operation_without_one(string tenant) =>
        Assert.Throws<ArgumentException>(() => new OperationContext(Actor.Person("ada"), tenant));

    [Fact]
    public void Work_an_operation_causes_carries_no_request_id()
    {
        var context = new OperationContext(Actor.Person("ada")) { RequestId = "r1" };

        Assert.Null(context.Continue().RequestId);
    }

    private static IServiceScope Begun(ServiceProvider host, string? requestId)
    {
        var scope = host.CreateScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.Person("ada")) { RequestId = requestId });
        return scope;
    }

    private static IDispatcher Dispatcher(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IDispatcher>();

    private static IReadOnlyList<string> Trace(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
}
