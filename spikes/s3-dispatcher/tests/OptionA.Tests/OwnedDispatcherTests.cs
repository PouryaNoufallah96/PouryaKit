using Microsoft.Extensions.DependencyInjection;
using OptionA.Dispatcher;
using OptionA.Module;
using Spike.Common;
using Xunit;

namespace OptionA.Tests;

// Not scanned by the Host below: it lives in the test assembly so the missing-handler test has an orphan.
internal sealed record OrphanCommand : ICommand<string>;

public sealed class OwnedDispatcherTests
{
    private static readonly ServiceProviderOptions Strict = new() { ValidateOnBuild = true, ValidateScopes = true };

    // The whole Host wiring: one call with the Module assembly. Nothing per handler, nothing per project.
    private static ServiceProvider BuildHost() =>
        new ServiceCollection().AddDispatcher(typeof(ThingsGateway).Assembly).BuildServiceProvider(Strict);

    [Fact]
    public async Task Command_runs_logging_authorization_validation_and_unit_of_work_in_order()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.CreateThing(scope.ServiceProvider.GetRequiredService<IDispatcher>(), "a");

        Assert.False(result.IsFailure);
        Assert.Equal(
        [
            "log:start CreateThingCommand", "authorize CreateThingCommand", "validate CreateThingCommand",
            "uow:begin", "handle CreateThingCommand", "uow:commit", "log:end CreateThingCommand",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }

    [Fact]
    public async Task Query_runs_the_same_pipeline_without_the_unit_of_work()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.GetThing(scope.ServiceProvider.GetRequiredService<IDispatcher>(), Guid.CreateVersion7());

        Assert.Equal("thing", result.Value);
        Assert.Equal(
        [
            "log:start GetThingQuery", "authorize GetThingQuery", "validate GetThingQuery",
            "handle GetThingQuery", "log:end GetThingQuery",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }

    [Theory]
    [InlineData(NestedOutcome.HandlerFails)]
    [InlineData(NestedOutcome.ValidationFails)]
    [InlineData(NestedOutcome.AuthorizationFails)]
    public async Task Ignored_nested_failure_of_any_kind_rolls_the_operation_back(NestedOutcome nested)
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(scope.ServiceProvider.GetRequiredService<IDispatcher>(), nested);

        Assert.Equal("outer done", result.Value); // the outer handler ignored the nested failure
        var steps = scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
        Assert.Equal(["uow:rollback", "log:end RunOuterCommand"], steps.TakeLast(2));
        Assert.DoesNotContain("uow:commit", steps);
    }

    [Fact]
    public async Task Successful_nested_command_joins_the_outer_unit_of_work_which_commits_once()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(scope.ServiceProvider.GetRequiredService<IDispatcher>(), NestedOutcome.Succeeds);

        Assert.Equal("outer done", result.Value);
        Assert.Equal(
        [
            "log:start RunOuterCommand", "uow:begin", "handle RunOuterCommand",
            "log:start RunNestedCommand", "uow:join", "handle RunNestedCommand", "log:end RunNestedCommand",
            "uow:commit", "log:end RunOuterCommand",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }

    [Fact]
    public void Missing_handler_fails_at_registration_before_the_container_is_built()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDispatcher(typeof(OrphanCommand).Assembly));

        Assert.Contains(typeof(OrphanCommand).FullName!, error.Message);
    }
}
