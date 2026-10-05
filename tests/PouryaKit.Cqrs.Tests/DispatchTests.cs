using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs.Tests.Module;

namespace PouryaKit.Cqrs.Tests;

// Every operation and handler here is an internal type of the test Module assembly, which grants no
// InternalsVisibleTo: each test also shows that the dispatcher finds them without it.
public sealed class DispatchTests
{
    [Fact]
    public async Task A_command_runs_logging_authorization_validation_and_the_unit_of_work_in_order()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.CreateThing(Dispatcher(scope));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "log:Running CreateThingCommand",
                "authorize CreateThingCommand",
                "validate CreateThingCommand",
                "uow:begin",
                "handle CreateThingCommand",
                "uow:commit",
                "log:Succeeded CreateThingCommand",
            ],
            Trace(scope)
        );
    }

    [Fact]
    public async Task A_query_runs_the_same_steps_without_the_unit_of_work()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.GetThing(Dispatcher(scope));

        Assert.Equal("thing", result.Value);
        Assert.Equal(
            [
                "log:Running GetThingQuery",
                "authorize GetThingQuery",
                "validate GetThingQuery",
                "handle GetThingQuery",
                "log:Succeeded GetThingQuery",
            ],
            Trace(scope)
        );
    }

    [Fact]
    public async Task With_no_authorizer_and_no_validation_package_a_command_goes_straight_to_its_unit_of_work()
    {
        await using var host = TestHost.Build(withValidationStep: false);
        using var scope = host.CreateScope();

        var result = await ThingsGateway.Ping(Dispatcher(scope));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["log:Running PingCommand", "uow:begin", "handle PingCommand", "uow:commit", "log:Succeeded PingCommand"],
            Trace(scope)
        );
    }

    [Theory]
    [InlineData(NestedOutcome.HandlerFails, "Nested.HandlerFails")]
    [InlineData(NestedOutcome.ValidationFails, "Nested.ValidationFails")]
    [InlineData(NestedOutcome.AuthorizationFails, "Nested.AuthorizationFails")]
    [InlineData(NestedOutcome.Throws, "Operation.CommandThrew")]
    public async Task A_nested_command_failure_the_outer_handler_ignores_still_rolls_the_operation_back(
        NestedOutcome nested,
        string errorCode
    )
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(Dispatcher(scope), nested);

        // The outer handler returned "outer done"; the Operation fails with the nested command's Errors instead.
        Assert.Equal(errorCode, Assert.Single(result.Errors).Code);
        Assert.Contains("uow:rollback", Trace(scope));
        Assert.DoesNotContain("uow:commit", Trace(scope));
    }

    [Fact]
    public async Task A_successful_nested_command_joins_the_outer_unit_of_work_which_commits_once()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(Dispatcher(scope), NestedOutcome.Succeeds);

        Assert.Equal("outer done", result.Value);
        Assert.Equal(
            [
                "log:Running RunOuterCommand",
                "validate RunOuterCommand",
                "uow:begin",
                "handle RunOuterCommand",
                "log:Running RunNestedCommand",
                "validate RunNestedCommand",
                "handle RunNestedCommand",
                "log:Succeeded RunNestedCommand",
                "uow:commit",
                "log:Succeeded RunOuterCommand",
            ],
            Trace(scope)
        );
    }

    [Fact]
    public async Task A_failed_nested_query_leaves_the_operation_free_to_commit()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuterIgnoringAFailedQuery(Dispatcher(scope));

        Assert.Equal("outer done", result.Value);
        Assert.Contains("log:Failed FindMissingThingQuery", Trace(scope));
        Assert.Contains("uow:commit", Trace(scope));
    }

    [Fact]
    public async Task A_query_handler_that_sends_a_command_throws()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.QuerySendingACommand(Dispatcher(scope)));

        Assert.DoesNotContain("uow:begin", Trace(scope));
        Assert.DoesNotContain("handle PingCommand", Trace(scope));
    }

    [Fact]
    public async Task A_second_outermost_command_in_the_same_scope_throws()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();
        await ThingsGateway.Ping(Dispatcher(scope));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.Ping(Dispatcher(scope)));

        Assert.Single(Trace(scope), step => step == "uow:begin");
    }

    [Fact]
    public async Task An_authorizer_that_sends_a_command_throws_before_the_nested_handler_runs()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThingsGateway.AuthorizeBySendingACommand(Dispatcher(scope))
        );

        Assert.DoesNotContain("handle PingCommand", Trace(scope));
        Assert.DoesNotContain("uow:begin", Trace(scope));
    }

    [Fact]
    public async Task A_nested_commands_authorizer_that_sends_a_command_throws_inside_the_unit_of_work()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThingsGateway.NestACommandWhoseAuthorizerSendsACommand(Dispatcher(scope))
        );

        Assert.DoesNotContain("handle PingCommand", Trace(scope));
        Assert.DoesNotContain("handle GuardedByCommandCommand", Trace(scope));
    }

    [Fact]
    public async Task An_authorizer_may_run_a_query_before_the_unit_of_work_starts()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.AuthorizeByRunningAQuery(Dispatcher(scope));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["handle GetThingQuery", "uow:begin", "handle GuardedByQueryCommand", "uow:commit"],
            Trace(scope)
                .Where(step =>
                    step.StartsWith("handle", StringComparison.Ordinal) || step.StartsWith("uow", StringComparison.Ordinal)
                )
        );
    }

    [Fact]
    public async Task A_command_exception_is_logged_once_with_its_exception_where_it_began()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThingsGateway.RunOuterLettingANestedExceptionThrough(Dispatcher(scope))
        );

        Assert.Equal(
            ["log:Threw RunNestedCommand InvalidOperationException"],
            Trace(scope).Where(step => step.StartsWith("log:Threw", StringComparison.Ordinal))
        );
    }

    [Fact]
    public async Task A_unit_of_work_that_reports_a_success_other_than_the_commands_result_throws()
    {
        await using var host = TestHost.Build(unitOfWork: typeof(SuccessReportingUnitOfWork));
        using var scope = host.CreateScope();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.CreateThing(Dispatcher(scope)));

        Assert.Contains(nameof(IUnitOfWork), error.Message);
    }

    private static IDispatcher Dispatcher(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IDispatcher>();

    private static IReadOnlyList<string> Trace(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
}
